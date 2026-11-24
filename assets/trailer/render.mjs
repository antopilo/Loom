// Renders the Loom trailer to MP4: headless Chrome (GPU WebGL) draws each frame, ffmpeg encodes them.
//
//   node assets/trailer/render.mjs                    full trailer with soundtrack
//   node assets/trailer/render.mjs --stills 3,12.5    PNG stills only
//   node assets/trailer/render.mjs --from 16 --to 28  a section
//   node assets/trailer/render.mjs --serve            serve the preview player at http://127.0.0.1:8737/
//
// Options: --fps 60, --workers 4, --out <file>, --no-audio. Output goes to artifacts/trailer/.
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

const TL = createRequire( import.meta.url )( './timeline.js' );

const here = path.dirname( fileURLToPath( import.meta.url ) );
const repo = path.resolve( here, '..', '..' );
const outDir = path.join( repo, 'artifacts', 'trailer' );
fs.mkdirSync( outDir, { recursive: true } );

const args = process.argv.slice( 2 );
const opt = ( name, def ) => { const i = args.indexOf( '--' + name ); return i >= 0 ? args[ i + 1 ] : def; };
const flag = name => args.includes( '--' + name );
const fps = Number( opt( 'fps', 60 ) );
const workers = Number( opt( 'workers', 4 ) );
const from = Number( opt( 'from', 0 ) );
const to = Number( opt( 'to', TL.DURATION ) );
const stills = opt( 'stills', null );
const out = path.resolve( opt( 'out', path.join( outDir, from === 0 && to === TL.DURATION ? 'loom-trailer.mp4' : `loom-trailer-${ from }-${ to }.mp4` ) ) );
const wav = path.join( outDir, 'loom-trailer.wav' );

/* ---------- static server: the page uses ES modules, which need http ---------- */

const MIME = { '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript', '.woff2': 'font/woff2', '.wav': 'audio/wav', '.mp4': 'video/mp4', '.png': 'image/png' };
function serve( port )
{
	const server = http.createServer( ( req, res ) =>
	{
		const url = decodeURIComponent( req.url.split( '?' )[ 0 ] );
		const file = url.startsWith( '/artifacts/' ) ? path.join( outDir, url.slice( '/artifacts/'.length ) ) : path.join( here, url === '/' ? 'index.html' : url );
		if ( !file.startsWith( here ) && !file.startsWith( outDir ) )
			return res.writeHead( 403 ).end();
		fs.readFile( file, ( err, data ) =>
		{
			if ( err )
				return res.writeHead( 404 ).end();
			res.writeHead( 200, { 'Content-Type': MIME[ path.extname( file ) ] || 'application/octet-stream', 'Cache-Control': 'no-store' } );
			res.end( data );
		} );
	} );
	return new Promise( r => server.listen( port, '127.0.0.1', () => r( server ) ) );
}

if ( flag( 'serve' ) )
{
	await serve( 8737 );
	console.log( 'SERVING http://127.0.0.1:8737/' );
}
else
	await main().catch( e => { console.error( '\nFAILED', e.message ); process.exitCode = 1; } );

async function main()
{
	const httpPort = 8800 + Math.floor( Math.random() * 400 );
	const server = await serve( httpPort );
	const chromes = [ 'C:/Program Files/Google/Chrome/Application/chrome.exe', 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe' ];
	const chrome = chromes.find( p => fs.existsSync( p ) );
	if ( !chrome )
		throw new Error( 'Chrome or Edge not found.' );
	const port = 9300 + Math.floor( Math.random() * 600 );
	const profile = fs.mkdtempSync( path.join( os.tmpdir(), 'loom-trailer-' ) );
	const browser = spawn( chrome, [
		'--headless=new', `--remote-debugging-port=${ port }`, `--user-data-dir=${ profile }`,
		'--window-size=1920,1080', '--hide-scrollbars', '--mute-audio', '--force-device-scale-factor=1',
		'--use-angle=d3d11', '--enable-gpu', '--ignore-gpu-blocklist',
		'--disable-background-timer-throttling', '--disable-renderer-backgrounding', '--disable-backgrounding-occluded-windows',
		'about:blank',
	], { stdio: 'ignore' } );
	const sleep = ms => new Promise( r => setTimeout( r, ms ) );
	try
	{
		for ( let i = 0; ; i++ )
		{
			try
			{
				if ( ( await fetch( `http://127.0.0.1:${ port }/json/version` ) ).ok )
					break;
			}
			catch { }
			if ( i > 100 )
				throw new Error( 'Chrome did not start.' );
			await sleep( 100 );
		}

		const connect = url => new Promise( ( resolve, reject ) =>
		{
			const ws = new WebSocket( url );
			let id = 0;
			const pending = new Map();
			ws.onmessage = e =>
			{
				const m = JSON.parse( e.data );
				if ( m.id && pending.has( m.id ) )
				{
					const p = pending.get( m.id );
					pending.delete( m.id );
					m.error ? p.reject( new Error( m.error.message ) ) : p.resolve( m.result );
				}
			};
			ws.onerror = reject;
			ws.onopen = () => resolve( {
				send: ( method, params = {} ) => new Promise( ( res, rej ) =>
				{
					pending.set( ++id, { resolve: res, reject: rej } );
					ws.send( JSON.stringify( { id, method, params } ) );
				} ),
			} );
		} );
		const openPage = async () =>
		{
			const info = await ( await fetch( `http://127.0.0.1:${ port }/json/new?about:blank`, { method: 'PUT' } ) ).json();
			const page = await connect( info.webSocketDebuggerUrl );
			await page.send( 'Runtime.enable' );
			await page.send( 'Page.enable' );
			await page.send( 'Page.navigate', { url: `http://127.0.0.1:${ httpPort }/index.html?render=1` } );
			for ( let i = 0; i < 400; i++ )
			{
				const r = await page.send( 'Runtime.evaluate', { expression: 'window.trailerReady !== undefined', returnByValue: true } );
				if ( r.result.value )
					break;
				await sleep( 50 );
			}
			const ready = await page.send( 'Runtime.evaluate', { expression: 'window.trailerReady', awaitPromise: true, returnByValue: true } );
			if ( ready.exceptionDetails )
				throw new Error( 'page failed: ' + ( ready.exceptionDetails.exception?.description || ready.exceptionDetails.text ) );
			return page;
		};
		const renderAt = async ( page, t ) =>
		{
			const r = await page.send( 'Runtime.evaluate', { expression: `renderFrame(${ t })`, awaitPromise: true, returnByValue: true } );
			if ( r.exceptionDetails )
				throw new Error( `frame ${ t }: ${ r.exceptionDetails.exception?.description || r.exceptionDetails.text }` );
			return Buffer.from( r.result.value.slice( r.result.value.indexOf( ',' ) + 1 ), 'base64' );
		};

		if ( stills )
		{
			const page = await openPage();
			const dir = path.join( outDir, 'stills' );
			fs.mkdirSync( dir, { recursive: true } );
			for ( const s of stills.split( ',' ) )
			{
				const t = Number( s );
				const file = path.join( dir, `t${ t.toFixed( 2 ).padStart( 5, '0' ) }.png` );
				fs.writeFileSync( file, await renderAt( page, t ) );
				console.log( file );
			}
			return;
		}

		const audio = !flag( 'no-audio' );
		if ( audio && !fs.existsSync( wav ) )
			await new Promise( ( res, rej ) => spawn( process.execPath, [ path.join( here, 'audio.mjs' ) ], { stdio: 'inherit' } ).on( 'exit', c => ( c ? rej( new Error( 'audio failed' ) ) : res() ) ) );
		const first = Math.round( from * fps ), total = Math.round( to * fps ) - first;
		const ffArgs = [ '-y', '-loglevel', 'error', '-f', 'image2pipe', '-framerate', String( fps ), '-c:v', 'png', '-i', '-' ];
		if ( audio )
			ffArgs.push( '-ss', String( from ), '-t', String( to - from ), '-i', wav );
		ffArgs.push( '-c:v', 'libx264', '-preset', 'slow', '-crf', '15', '-pix_fmt', 'yuv420p', '-movflags', '+faststart' );
		if ( audio )
			ffArgs.push( '-c:a', 'aac', '-b:a', '256k', '-shortest' );
		ffArgs.push( out );
		const ff = spawn( 'ffmpeg', ffArgs, { stdio: [ 'pipe', 'inherit', 'inherit' ] } );
		const ffDone = new Promise( ( res, rej ) => ff.on( 'exit', c => ( c ? rej( new Error( 'ffmpeg exit ' + c ) ) : res() ) ) );

		const pages = await Promise.all( Array.from( { length: workers }, openPage ) );
		const done = new Map(), wakers = [];
		let nextTake = 0, nextWrite = 0, writing = Promise.resolve();
		const started = Date.now();
		const write = async () =>
		{
			while ( done.has( nextWrite ) )
			{
				const buf = done.get( nextWrite );
				done.delete( nextWrite );
				nextWrite++;
				if ( !ff.stdin.write( buf ) )
					await new Promise( r => ff.stdin.once( 'drain', r ) );
				if ( nextWrite % 60 === 0 || nextWrite === total )
				{
					const el = ( Date.now() - started ) / 1000;
					process.stdout.write( `\rframe ${ nextWrite }/${ total }  ${ ( nextWrite / el ).toFixed( 1 ) } fps  eta ${ Math.round( ( total - nextWrite ) / ( nextWrite / el ) ) }s   ` );
				}
			}
		};
		await Promise.all( pages.map( async page =>
		{
			while ( nextTake < total )
			{
				while ( done.size > workers * 6 )
					await new Promise( r => wakers.push( r ) );
				const i = nextTake++;
				done.set( i, await renderAt( page, ( first + i ) / fps ) );
				writing = writing.then( write ).then( () => { for ( const w of wakers.splice( 0 ) ) w(); } );
			}
		} ) );
		await writing;
		ff.stdin.end();
		await ffDone;
		console.log( `\nRENDERED ${ out }` );
	}
	finally
	{
		browser.kill();
		server.close();
		await sleep( 300 );
		try { fs.rmSync( profile, { recursive: true, force: true } ); } catch { }
	}
}
