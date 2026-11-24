// Parts of the Loom trailer: Loom UI moments (docking, an agent turn, module panels, the phone client) that film.js stages.
( function ( root )
{
	'use strict';

	const L = root.LK;
	const { W, TAU, FD, FT, FM, clamp, seg, lerp, frac, E, spring, rnd } = L;
	const P0 = L.LOOM;
	let c = null;

	function pointAt( keys, t )
	{
		if ( t <= keys[ 0 ][ 0 ] )
			return [ keys[ 0 ][ 1 ], keys[ 0 ][ 2 ] ];
		for ( let i = 0; i < keys.length - 1; i++ )
		{
			const a = keys[ i ], b = keys[ i + 1 ];
			if ( t < b[ 0 ] )
			{
				const p = E.inOutCubic( seg( t, a[ 0 ], b[ 0 ] ) );
				return [ lerp( a[ 1 ], b[ 1 ], p ), lerp( a[ 2 ], b[ 2 ], p ) ];
			}
		}
		const z = keys[ keys.length - 1 ];
		return [ z[ 1 ], z[ 2 ] ];
	}

	/* ======================================================================
	   The agent drives the app: its terminal, and the pages its browser shows
	   ====================================================================== */

	/** Terminal lines of the pricing turn, at film times. */
	const TERM_PRICING = [
		{ at: 9.0, s: 'npm run build', prompt: 'F:\\website>', cps: 40 },
		{ at: 9.55, s: '  vite v6.2  building for production…', color: 'muted' },
		{ at: 10.1, s: '  ✓ 42 pages in 3.1 s', color: 'success' },
		{ at: 11.2, s: 'npx lighthouse :4321/pricing', prompt: 'F:\\website>', cps: 40 },
		{ at: 11.95, s: '  LCP   2.7 s → 0.9 s', color: 'success', bold: true },
		{ at: 12.15, s: '  JS    412 KB → 96 KB', color: 'success' },
		{ at: 12.35, s: '', prompt: 'F:\\website>' },
	];

	const WEB = { bg: '#fbfaf7', ink: '#1f2328', mute: '#6b7280', line: '#e7e5e0', brand: '#5b5bd6' };

	/** Draws fn( width ) into r with the page scaled to fit a 620-unit layout. */
	function page( r, fn )
	{
		c.fillStyle = WEB.bg;
		c.fillRect( r.x, r.y, r.w, r.h );
		const k = Math.min( 1.15, r.w / 620 );
		c.save();
		c.translate( r.x, r.y );
		c.scale( k, k );
		fn( r.w / k );
		c.restore();
	}

	/** The site's pricing page; it loads from t0. */
	function pricingPage( r, t, t0 )
	{
		page( r, pw =>
		{
			const loaded = E.outCubic( seg( t, t0 + 0.4, t0 + 0.8 ) );
			L.dot( 28, 26, 7, WEB.brand );
			L.txt( 'Acme', 42, 31, 15, WEB.ink, { w: 700 } );
			[ 'Product', 'Docs', 'Pricing' ].forEach( ( s, i ) => L.txt( s, pw - 220 + i * 68, 31, 12.5, i === 2 ? WEB.ink : WEB.mute, { w: i === 2 ? 600 : 400 } ) );
			L.line( 0, 52, pw, 52, WEB.line );
			L.alpha( 1 - loaded, () =>
			{
				L.fillR( pw / 2 - 80, 84, 160, 22, 6, '#ebe9e4' );
				for ( let i = 0; i < 3; i++ )
					L.fillR( 24 + i * ( pw - 48 ) / 3, 156, ( pw - 72 ) / 3, 190, 10, '#f1efea' );
			} );
			L.alpha( loaded, () =>
			{
				L.txt( 'Pricing', pw / 2, 104, 34, WEB.ink, { w: 700, align: 'center' } );
				L.txt( 'Simple plans for teams of any size.', pw / 2, 132, 14, WEB.mute, { align: 'center' } );
				const cw = ( pw - 72 ) / 3;
				[ [ 'Starter', '$0' ], [ 'Team', '$12' ], [ 'Scale', '$29' ] ].forEach( ( p, i ) =>
				{
					const x = 24 + i * ( cw + 12 ), y = 156 + ( 1 - loaded ) * 10, hl = i === 1;
					L.fillR( x, y, cw, 190, 10, hl ? '#f1f0ff' : '#ffffff' );
					L.strokeR( x + 0.5, y + 0.5, cw - 1, 189, 10, hl ? WEB.brand : WEB.line, hl ? 1.5 : 1 );
					L.txt( p[ 0 ], x + 16, y + 28, 13, WEB.mute, { w: 600 } );
					L.txt( p[ 1 ], x + 16, y + 66, 30, WEB.ink, { w: 700 } );
					L.txt( '/ month', x + 22 + L.measure( p[ 1 ], 30, 700 ), y + 66, 12, WEB.mute );
					for ( let j = 0; j < 3; j++ )
						L.fillR( x + 16, y + 90 + j * 16, cw * ( 0.7 - j * 0.12 ), 6, 3, '#d9d6cf' );
					L.fillR( x + 16, y + 146, cw - 32, 30, 8, hl ? WEB.brand : '#efede8' );
					L.txt( 'Choose', x + cw / 2, y + 166, 12.5, hl ? '#ffffff' : WEB.ink, { w: 600, align: 'center' } );
				} );
			} );
		} );
	}

	const REPORT = [ [ 'Largest paint', '2.7 s', '0.9 s' ], [ 'JavaScript', '412 KB', '96 KB' ], [ 'Requests', '58', '21' ], [ 'Score', '61', '98' ] ];
	/** The before-and-after report the agent writes and shows; rows land from t0. */
	function reportPage( r, t, t0 )
	{
		page( r, pw =>
		{
			L.txt( 'Pricing page', 28, 50, 26, WEB.ink, { w: 700 } );
			L.txt( 'Before and after  ·  Lighthouse, 5 runs', 28, 76, 13, WEB.mute );
			const cb = pw * 0.56, ca = pw * 0.8;
			L.txt( 'Metric', 28, 112, 12, WEB.mute, { w: 600 } );
			L.txt( 'Before', cb, 112, 12, WEB.mute, { w: 600 } );
			L.txt( 'After', ca, 112, 12, WEB.mute, { w: 600 } );
			REPORT.forEach( ( row, i ) =>
			{
				const a = E.outCubic( seg( t, t0 + 0.2 + i * 0.15, t0 + 0.55 + i * 0.15 ) );
				const y = 124 + i * 44, big = i === REPORT.length - 1;
				L.line( 24, y, pw - 24, y, WEB.line );
				L.alpha( a, () =>
				{
					const fs = big ? 22 : 15;
					L.txt( row[ 0 ], 28 + ( 1 - a ) * 10, y + 28, 15, WEB.ink, { w: big ? 700 : 400 } );
					L.txt( row[ 1 ], cb, y + 28, fs, '#b4534b', { w: big ? 700 : 400 } );
					L.txt( row[ 2 ], ca, y + 28, fs, '#2f7d4f', { w: 700 } );
				} );
			} );
		} );
	}

	/* ======================================================================
	   Built-in modules as Settings tiles
	   ====================================================================== */

	const TILES = [
		[ 'Omp', 'spark', 'omp' ], [ 'Conversations', 'chat', 'conversations' ], [ 'Projects', 'folder', 'projects' ],
		[ 'Workspaces', 'grid', 'workspaces' ], [ 'Git', 'git', 'git' ], [ 'GitHub', 'pr', 'github' ],
		[ 'Worktrees', 'branch', 'worktrees' ], [ 'Terminal', 'term', 'terminal' ], [ 'Files', 'folder', 'files' ],
		[ 'Editor', 'code', 'editor' ], [ 'Markdown', 'doc', 'markdown' ], [ 'Browser', 'globe', 'browser' ],
		[ 'Notifications', 'bell', 'notifications' ], [ 'Remote', 'monitor', 'remote' ], [ 'Get started', 'check', 'setup' ],
		[ 'Hot reload', 'refresh', 'hotload' ], [ 'Sketch', 'nodes', 'sketch' ], [ 'Tracy', 'flame', 'tracy' ],
	];

	/** One module tile, w x h at (x, y); on runs 0 (off) … 1 (on). */
	function moduleTile( x, y, w, h, tile, on, P )
	{
		L.shadow( 24, 8, 0.25, () => L.fillR( x, y, w, h, 12, P.raised ) );
		L.strokeR( x + 0.5, y + 0.5, w - 1, h - 1, 12, P.border );
		L.alpha( lerp( 0.45, 1, on ), () =>
		{
			L.fillR( x + 16, y + 16, 34, 34, 8, P.hover );
			L.icon( tile[ 1 ], x + 24, y + 24, 18, P.muted );
			L.txt( tile[ 0 ], x + 16, y + h - 32, 17, P.text, { w: 600 } );
			L.txt( tile[ 2 ], x + 16, y + h - 13, 12, P.muted, { f: FM } );
		} );
		L.toggle( x + w - 56, y + 22, on, P );
	}

	const CODE = [
		'using Lokn.Core;',
		'',
		'public sealed class NotesModule : ILoknModule',
		'{',
		'    public string Id => "notes";',
		'    public string Title => "Notes";',
		'',
		'    public void Register( ModuleBuilder builder )',
		'    {',
		'        builder.Panel( "note", "Note",',
		'            context => new NoteContent( context ) );',
		'    }',
		'}',
	];
	const KW = new Set( [ 'using', 'public', 'sealed', 'class', 'string', 'void', 'new', 'internal', 'override', 'return' ] );
	const TYPES = new Set( [ 'ILoknModule', 'ModuleBuilder', 'NoteContent', 'NotesModule' ] );
	function codeLine( s, x, y, fs, P )
	{
		const re = /("[^"]*"?)|([A-Za-z_][A-Za-z0-9_]*)|(\s+)|(=>)|(.)/g;
		let m, cx = x;
		while ( ( m = re.exec( s ) ) )
		{
			const tok = m[ 0 ];
			let col = P.text;
			if ( m[ 1 ] )
				col = P.success;
			else if ( m[ 2 ] )
				col = KW.has( tok ) ? P.magenta : TYPES.has( tok ) ? P.cyan : /^[A-Z]/.test( tok ) ? P.yellow : P.text;
			else if ( m[ 4 ] )
				col = P.accent;
			else if ( m[ 5 ] )
				col = P.muted;
			if ( !m[ 3 ] )
				L.txt( tok, cx, y, fs, col, { f: FM } );
			cx += L.measure( tok, fs, 400, FM );
		}
		return cx;
	}

	const MONTAGE = [
		[ 'Sketch', 'nodes', 'sketch' ], [ 'Tracy profiler', 'flame', 'tracy' ], [ 'Now Playing', 'music', 'nowplaying' ],
		[ 's&box', 'cube', 'sbox' ], [ 'Pomodoro', 'timer', 'pomodoro' ], [ 'Kanban', 'kanban', 'kanban' ],
		[ 'System monitor', 'chart', 'sysmon' ], [ 'Weather', 'sun', 'weather' ], [ 'Pixel editor', 'pixel', 'pixels' ],
		[ 'API client', 'api', 'api' ], [ 'Notepad', 'note', 'notepad' ], [ 'Your module', 'plus', 'yours' ],
	];

	function drawEditor( t, r )
	{
		const P = P0;
		const saved = t >= 35.15;
		const cr = L.panel( r.x, r.y, r.w, r.h, P, { title: 'NotesModule.cs' + ( saved ? '' : '  ●' ), icon: 'code', right: '%LOCALAPPDATA%\\Lokn\\modules\\Notes', shadowBlur: 60, shadowY: 24, shadowA: 0.5 } );
		const flash = 1 - seg( t, 35.6, 36.3 );
		if ( t >= 35.6 )
			L.glow( P.success, 30 * flash, () => L.strokeR( r.x, r.y, r.w, r.h, 10, P.success, 2, flash ) );
		c.save();
		L.clipR( cr.x, cr.y, cr.w, cr.h - 36, 0 );
		const fs = 19, lh = 31, x0 = cr.x + 64, y0 = cr.y + 42;
		let budget = Math.floor( ( t - 33.4 ) * 210 );
		let caret = null;
		CODE.forEach( ( ln, i ) =>
		{
			const y = y0 + i * lh;
			L.txt( String( i + 1 ), cr.x + 40, y, 15, P.inactive, { f: FM, align: 'right' } );
			if ( budget < 0 )
				return;
			const s = ln.slice( 0, Math.max( 0, budget ) );
			const end = codeLine( s, x0, y, fs, P );
			if ( budget <= ln.length )
				caret = [ end, y ];
			budget -= ln.length + 1;
		} );
		if ( !caret )
			caret = [ x0 + L.measure( CODE[ CODE.length - 1 ], fs, 400, FM ), y0 + ( CODE.length - 1 ) * lh ];
		const typing = t < 35.0;
		if ( t >= 33.4 && ( typing || frac( t * 1.8 ) < 0.6 ) )
			L.fillR( caret[ 0 ] + 1, caret[ 1 ] - 17, 2, 23, 0, P.accent );
		c.restore();
		// Status bar
		const sy = cr.y + cr.h - 36;
		L.line( cr.x, sy, cr.x + cr.w, sy, P.divider );
		if ( t >= 35.2 && t < 35.6 )
		{
			L.spinner( cr.x + 22, sy + 18, 7, t, P.accent );
			L.txt( 'Compiling notes…', cr.x + 38, sy + 23, 14, P.muted );
		}
		else if ( t >= 35.6 )
		{
			L.dot( cr.x + 22, sy + 18, 5, P.success );
			L.txt( 'Reloaded notes  ·  184 ms', cr.x + 38, sy + 23, 14, P.success, { w: 600 } );
		}
		else
			L.txt( 'C#  ·  source module', cr.x + 18, sy + 23, 14, P.muted );
		L.txt( `Ln ${ Math.min( 13, Math.floor( clamp( ( t - 33.4 ) / 1.6 ) * 12 ) + 1 ) }`, cr.x + cr.w - 18, sy + 23, 13, P.muted, { f: FM, align: 'right' } );
	}

	let pixelMask = null;
	function getPixelMask()
	{
		if ( pixelMask )
			return pixelMask;
		const n = 24, cv = document.createElement( 'canvas' );
		cv.width = cv.height = n;
		const g = cv.getContext( '2d' ), prev = L.ctx();
		L.use( g );
		L.logo( 12, 12, 23, { ring: '#ffffff', thread: '#ff0000' } );
		L.use( prev );
		const d = g.getImageData( 0, 0, n, n ).data;
		pixelMask = [];
		for ( let y = 0; y < n; y++ )
			for ( let x = 0; x < n; x++ )
			{
				const i = ( y * n + x ) * 4;
				if ( d[ i + 3 ] > 110 )
					pixelMask.push( { x, y, thread: d[ i + 1 ] < 128 } );
			}
		return pixelMask;
	}

	const MONTAGE_DRAW = {
		sketch( r, lt, t )
		{
			const P = P0;
			for ( let x = r.x + 14; x < r.x + r.w; x += 28 )
				for ( let y = r.y + 14; y < r.y + r.h; y += 28 )
					L.dot( x, y, 1.2, P.muted, 0.18 );
			const nodes = [
				{ x: 0.1, y: 0.12, title: 'Breakfast', tag: 'CLASS', rows: [ 'serve()', 'eggs: int' ] },
				{ x: 0.42, y: 0.08, title: 'Brunch', tag: 'CLASS', rows: [ 'drink()', 'mimosa: bool' ] },
				{ x: 0.26, y: 0.56, title: 'benedict', tag: 'INSTANCE', rows: [ 'meat = "ham"' ] },
				{ x: 0.72, y: 0.48, title: 'Menu', tag: 'INTERFACE', rows: [ 'items()' ] },
			];
			const box = n => ( { x: r.x + n.x * r.w, y: r.y + n.y * r.h, w: 250, h: 52 + n.rows.length * 30 } );
			const edges = [ [ 2, 0 ], [ 2, 1 ], [ 1, 3 ], [ 0, 3 ] ];
			edges.forEach( ( e, i ) =>
			{
				const a = box( nodes[ e[ 0 ] ] ), b = box( nodes[ e[ 1 ] ] );
				const p0 = [ a.x + a.w / 2, a.y + ( e[ 0 ] === 2 ? 0 : a.h / 2 ) ];
				const p3 = e[ 1 ] === 3 ? [ b.x, b.y + b.h / 2 ] : [ b.x + b.w / 2, b.y + b.h ];
				const pts = L.bez( p0, [ p0[ 0 ], lerp( p0[ 1 ], p3[ 1 ], 0.5 ) ], [ lerp( p0[ 0 ], p3[ 0 ], 0.6 ), p3[ 1 ] ], p3, 30 );
				const poly = new L.Poly( pts );
				const p = E.outCubic( seg( lt, 0.12 + i * 0.05, 0.42 + i * 0.05 ) );
				poly.stroke( 0, poly.len * p, 2, P.accent, 0.9 );
				if ( p > 0.98 )
				{
					const end = poly.at( poly.len ), back = poly.at( poly.len - 12 );
					const an = Math.atan2( end[ 1 ] - back[ 1 ], end[ 0 ] - back[ 0 ] );
					c.beginPath();
					c.moveTo( end[ 0 ], end[ 1 ] );
					c.lineTo( end[ 0 ] - Math.cos( an - 0.45 ) * 12, end[ 1 ] - Math.sin( an - 0.45 ) * 12 );
					c.lineTo( end[ 0 ] - Math.cos( an + 0.45 ) * 12, end[ 1 ] - Math.sin( an + 0.45 ) * 12 );
					c.fillStyle = L.css( P.accent );
					c.fill();
				}
			} );
			nodes.forEach( ( n, i ) =>
			{
				const b = box( n ), p = seg( lt, i * 0.05, i * 0.05 + 0.3 );
				L.alpha( clamp( p * 3 ), () => L.scaled( lerp( 0.7, 1, E.outBack( p ) ), b.x + b.w / 2, b.y + b.h / 2, () =>
				{
					L.shadow( 20, 8, 0.35, () => L.fillR( b.x, b.y, b.w, b.h, 10, P.raised ) );
					L.strokeR( b.x + 0.5, b.y + 0.5, b.w - 1, b.h - 1, 10, P.border );
					L.txt( n.tag, b.x + 16, b.y + 20, 10.5, P.accent, { f: FM, w: 600, ls: 1.5 } );
					L.txt( n.title, b.x + 16, b.y + 42, 17, P.text, { w: 600 } );
					L.line( b.x, b.y + 52, b.x + b.w, b.y + 52, P.divider );
					n.rows.forEach( ( row, j ) => L.txt( row, b.x + 16, b.y + 74 + j * 30, 13.5, P.muted, { f: FM } ) );
				} ) );
			} );
		},

		tracy( r, lt, t )
		{
			const P = P0;
			c.fillStyle = L.css( P.term );
			c.fillRect( r.x, r.y, r.w, r.h );
			L.txt( 'Frame 18 342', r.x + 20, r.y + 28, 14, P.text, { f: FM, w: 600 } );
			L.txt( '6.9 ms  ·  144 FPS', r.x + 160, r.y + 28, 14, P.success, { f: FM } );
			const nb = 120, bw = ( r.w - 40 ) / nb, off = Math.floor( t * 30 );
			for ( let i = 0; i < nb; i++ )
			{
				const v = 0.25 + rnd( i + off, 11 ) * 0.45 + ( rnd( i + off, 12 ) > 0.95 ? 0.35 : 0 );
				const col = v > 0.8 ? P.error : v > 0.6 ? P.yellow : P.success;
				const bh = v * 90 * E.outExpo( seg( lt, i * 0.002, i * 0.002 + 0.3 ) );
				L.fillR( r.x + 20 + i * bw, r.y + 140 - bh, bw - 2, bh, 1, col, 0.85 );
			}
			const rows = [ 'Main thread', 'Render', 'Worker 1', 'Worker 2', 'GPU' ];
			const names = [ 'Update', 'Physics', 'Render', 'Present', 'Audio', 'Jobs', 'Culling', 'Shadows', 'UI', 'Animate' ];
			const cols = [ P.accent, P.blue, P.success, P.magenta, P.cyan, P.yellow ];
			const shift = t * 140;
			rows.forEach( ( name, ri ) =>
			{
				const y = r.y + 170 + ri * 82;
				L.txt( name, r.x + 20, y + 22, 12.5, P.muted );
				c.save();
				L.clipR( r.x + 120, y, r.w - 140, 76, 0 );
				let x = r.x + 120 - ( shift % 900 ) - 900;
				let k = 0;
				while ( x < r.x + r.w )
				{
					const w = 60 + rnd( k, ri + 20 ) * 200;
					const depth = 1 + Math.floor( rnd( k, ri + 40 ) * 3 );
					for ( let d = 0; d < depth; d++ )
					{
						const ww = w * ( 1 - d * 0.28 ), xx = x + d * 10;
						const col = cols[ ( k + d + ri ) % cols.length ];
						L.fillR( xx, y + 6 + d * 22, ww - 3, 19, 3, col, 0.85 - d * 0.12 );
						if ( ww > 70 )
							L.txt( names[ ( k * 3 + d + ri ) % names.length ], xx + 7, y + 20 + d * 22, 11.5, '#1a1917', { w: 600 } );
					}
					x += w + 6;
					k++;
				}
				c.restore();
			} );
		},

		nowplaying( r, lt, t )
		{
			const P = P0;
			const ax = r.x + 70, ay = r.y + 80, as = 440;
			const g = c.createLinearGradient( ax, ay, ax + as, ay + as );
			g.addColorStop( 0, '#d5a583' );
			g.addColorStop( 0.55, '#b0708c' );
			g.addColorStop( 1, '#3c3a6e' );
			L.shadow( 40, 18, 0.5, () => L.fillR( ax, ay, as, as, 18, '#000' ) );
			L.fillR( ax, ay, as, as, 18, '#000' );
			c.fillStyle = g;
			c.fill();
			c.save();
			L.clipR( ax, ay, as, as, 18 );
			for ( let i = 0; i < 7; i++ )
				L.ring( ax + as * 0.5, ay + as * 0.5, 30 + i * 34 + Math.sin( t * 2 + i ) * 4, '#fff3e6', 2, 0.18 );
			L.logo( ax + as / 2, ay + as / 2, 150, { ring: '#fff6ec', thread: '#2a2035' } );
			c.restore();
			const tx = ax + as + 70;
			L.txt( 'NOW PLAYING', tx, ay + 40, 15, P.accent, { f: FM, w: 600, ls: 4 } );
			L.txt( 'Weaving Lights', tx, ay + 120, 64, P.text, { w: 700, f: FD, ls: -1.5 } );
			L.txt( 'Night Shift  ·  Threads', tx, ay + 165, 24, P.muted, { f: FD } );
			const pw = r.x + r.w - 80 - tx, prog = 0.42 + ( t - 37 ) * 0.01;
			L.fillR( tx, ay + 220, pw, 6, 3, P.hover );
			L.fillR( tx, ay + 220, pw * prog, 6, 3, P.accent );
			L.dot( tx + pw * prog, ay + 223, 9, P.text );
			L.txt( '1:42', tx, ay + 252, 14, P.muted, { f: FM } );
			L.txt( '3:58', tx + pw, ay + 252, 14, P.muted, { f: FM, align: 'right' } );
			const nb = 48, bw = pw / nb;
			for ( let i = 0; i < nb; i++ )
			{
				const v = 0.15 + 0.85 * Math.abs( Math.sin( t * ( 3 + rnd( i, 31 ) * 5 ) + i * 0.7 ) ) * ( 0.4 + 0.6 * rnd( i, 32 ) );
				const bh = v * 150 * E.outExpo( seg( lt, 0, 0.3 ) );
				L.fillR( tx + i * bw, ay + 440 - bh, bw - 4, bh, 3, L.mix( P.accent, P.magenta, i / nb ), 0.9 );
			}
		},

		sbox( r, lt, t )
		{
			const P = P0;
			const g = c.createLinearGradient( 0, r.y, 0, r.y + r.h );
			g.addColorStop( 0, '#1f2633' );
			g.addColorStop( 0.5, '#2c3240' );
			g.addColorStop( 1, '#161a22' );
			c.fillStyle = g;
			c.fillRect( r.x, r.y, r.w, r.h );
			const hz = r.y + r.h * 0.48, cx = r.x + r.w / 2, f = 420;
			c.save();
			c.lineWidth = 1;
			for ( let i = -14; i <= 14; i++ )
			{
				c.beginPath();
				c.moveTo( cx + i * 60, r.y + r.h );
				c.lineTo( cx + i * 6, hz );
				c.strokeStyle = 'rgba(131,168,212,0.25)';
				c.stroke();
			}
			const sh = frac( t * 0.8 );
			for ( let z = 1; z < 24; z++ )
			{
				const zz = z - sh;
				if ( zz <= 0.2 )
					continue;
				const y = hz + f * 0.55 / zz;
				if ( y > r.y + r.h )
					continue;
				c.beginPath();
				c.moveTo( r.x, y );
				c.lineTo( r.x + r.w, y );
				c.strokeStyle = `rgba(131,168,212,${ 0.3 / Math.sqrt( zz ) })`;
				c.stroke();
			}
			c.restore();
			const ay = t * 0.9, ax = 0.5 + Math.sin( t * 0.6 ) * 0.2;
			const V = [];
			for ( let i = 0; i < 8; i++ )
			{
				let x = ( i & 1 ? 1 : -1 ), y = ( i & 2 ? 1 : -1 ), z = ( i & 4 ? 1 : -1 );
				let x1 = x * Math.cos( ay ) - z * Math.sin( ay ), z1 = x * Math.sin( ay ) + z * Math.cos( ay );
				let y1 = y * Math.cos( ax ) - z1 * Math.sin( ax ), z2 = y * Math.sin( ax ) + z1 * Math.cos( ax );
				const d = 5.2 + z2;
				V.push( [ cx + x1 * f / d, r.y + r.h * 0.44 + y1 * f / d ] );
			}
			const ed = [ [ 0, 1 ], [ 2, 3 ], [ 4, 5 ], [ 6, 7 ], [ 0, 2 ], [ 1, 3 ], [ 4, 6 ], [ 5, 7 ], [ 0, 4 ], [ 1, 5 ], [ 2, 6 ], [ 3, 7 ] ];
			const sp = E.outBack( seg( lt, 0, 0.35 ) );
			L.scaled( sp, cx, r.y + r.h * 0.44, () =>
			{
				L.glow( P.accent, 18, () =>
				{
					c.beginPath();
					for ( const [ a, b ] of ed )
					{
						c.moveTo( V[ a ][ 0 ], V[ a ][ 1 ] );
						c.lineTo( V[ b ][ 0 ], V[ b ][ 1 ] );
					}
					c.strokeStyle = L.css( P.accent );
					c.lineWidth = 3;
					c.stroke();
				} );
				for ( const v of V )
					L.dot( v[ 0 ], v[ 1 ], 4, '#fff1e4' );
			} );
			const chips = [ [ '▶  Play', P.accent ], [ 'Build ✓  1.4 s', P.success ], [ 'arena.scene', P.muted ] ];
			let x = r.x + 20;
			for ( const [ label, col ] of chips )
			{
				const w = L.measure( label, 14, 600 ) + 28;
				L.fillR( x, r.y + 18, w, 32, 8, '#11141a', 0.8 );
				L.txt( label, x + 14, r.y + 39, 14, col, { w: 600 } );
				x += w + 10;
			}
			L.txt( '144 FPS', r.x + r.w - 20, r.y + 39, 14, P.success, { f: FM, align: 'right' } );
		},

		pomodoro( r, lt, t )
		{
			const P = P0;
			const cx = r.x + r.w / 2, cy = r.y + r.h / 2 + 10, R = 230;
			L.ring( cx, cy, R, P.hover, 18 );
			const p = 0.18 + E.outExpo( seg( lt, 0, 0.45 ) ) * 0.64;
			c.save();
			c.beginPath();
			c.arc( cx, cy, R, -Math.PI / 2, -Math.PI / 2 + p * TAU );
			c.strokeStyle = L.css( P.accent );
			c.lineWidth = 18;
			c.lineCap = 'round';
			c.shadowColor = L.css( P.accent );
			c.shadowBlur = 24;
			c.stroke();
			c.restore();
			const secs = 24 * 60 + 59 - Math.floor( lt * 6 );
			L.txt( `${ Math.floor( secs / 60 ) }:${ String( secs % 60 ).padStart( 2, '0' ) }`, cx, cy + 40, 128, P.text, { f: FM, w: 600, align: 'center' } );
			L.txt( 'FOCUS  ·  2 OF 4', cx, cy + 96, 18, P.muted, { f: FM, w: 600, ls: 4, align: 'center' } );
			for ( let i = 0; i < 4; i++ )
				L.dot( cx - 45 + i * 30, cy - 135, 7, i < 2 ? P.accent : P.hover );
		},

		kanban( r, lt, t )
		{
			const P = P0;
			const cols = [ [ 'To do', [ [ 'Write release notes', P.blue ], [ 'Ask Omp for tests', P.magenta ], [ 'Remote: QR pairing', P.cyan ] ] ],
				[ 'Doing', [ [ 'Phone push', P.accent ], [ 'Trailer music', P.yellow ] ] ],
				[ 'Done', [ [ 'Theme sweep', P.success ], [ 'Hot reload', P.success ] ] ] ];
			const cw = ( r.w - 80 ) / 3;
			const mv = spring( lt - 0.08, 1.2, 7 );
			cols.forEach( ( [ name, cards ], ci ) =>
			{
				const x = r.x + 20 + ci * ( cw + 20 );
				L.fillR( x, r.y + 20, cw, r.h - 40, 12, P.canvas );
				L.txt( name, x + 18, r.y + 52, 17, P.text, { w: 600 } );
				L.txt( String( cards.length + ( ci === 2 ? ( mv > 0.5 ? 1 : 0 ) : ci === 1 ? ( mv > 0.5 ? -1 : 0 ) : 0 ) ), x + cw - 18, r.y + 52, 14, P.muted, { f: FM, align: 'right' } );
				cards.forEach( ( [ title, col ], i ) =>
				{
					if ( ci === 1 && i === 0 )
						return;
					const y = r.y + 76 + ( ci === 2 ? i + 1 : ci === 1 ? i - 1 : i ) * 96 + ( ci === 2 ? ( 1 - clamp( mv ) ) * -96 : 0 );
					kanbanCard( x + 12, ci === 1 ? r.y + 76 + 96 * ( 1 - clamp( mv ) ) : y, cw - 24, title, col, P, 0 );
				} );
			} );
			const from = [ r.x + 20 + cw + 20 + 12, r.y + 76 ], to = [ r.x + 20 + 2 * ( cw + 20 ) + 12, r.y + 76 ];
			const lift = Math.sin( clamp( mv ) * Math.PI );
			kanbanCard( lerp( from[ 0 ], to[ 0 ], mv ), lerp( from[ 1 ], to[ 1 ], mv ) - lift * 24, cw - 24, 'Phone push', P.accent, P, lift );
		},

		sysmon( r, lt, t )
		{
			const P = P0;
			const charts = [ [ 'CPU', P.accent, 0, '%' ], [ 'GPU', P.blue, 1, '%' ], [ 'Memory', P.success, 2, ' GB' ], [ 'Network', P.magenta, 3, ' MB/s' ] ];
			const cw = ( r.w - 60 ) / 2, ch = ( r.h - 60 ) / 2;
			charts.forEach( ( [ name, col, k, unit ], i ) =>
			{
				const x = r.x + 20 + ( i % 2 ) * ( cw + 20 ), y = r.y + 20 + Math.floor( i / 2 ) * ( ch + 20 );
				L.fillR( x, y, cw, ch, 12, P.canvas );
				const val = k => 0.45 + 0.25 * Math.sin( k * 0.21 + i ) + 0.15 * Math.sin( k * 0.73 + i * 2 ) + 0.08 * ( rnd( Math.floor( k ), i + 50 ) - 0.5 );
				const now = val( t * 12 );
				L.txt( name, x + 20, y + 34, 16, P.muted, { w: 600 } );
				const shown = k === 2 ? ( 8 + now * 8 ).toFixed( 1 ) : k === 3 ? ( now * 120 ).toFixed( 0 ) : ( now * 100 ).toFixed( 0 );
				L.txt( shown + unit, x + cw - 20, y + 40, 30, P.text, { f: FD, w: 700, align: 'right' } );
				const n = 80, gx = x + 20, gw = cw - 40, gy = y + ch - 20, gh = ch - 80;
				const grow = E.outExpo( seg( lt, 0, 0.4 ) );
				c.beginPath();
				for ( let j = 0; j <= n; j++ )
				{
					const v = val( t * 12 - ( n - j ) * 0.6 );
					const px = gx + j / n * gw, py = gy - v * gh * grow;
					if ( j === 0 )
						c.moveTo( px, py );
					else
						c.lineTo( px, py );
				}
				const g = c.createLinearGradient( 0, gy - gh, 0, gy );
				g.addColorStop( 0, L.css( col, 0.35 ) );
				g.addColorStop( 1, L.css( col, 0 ) );
				c.strokeStyle = L.css( col );
				c.lineWidth = 2.5;
				c.stroke();
				c.lineTo( gx + gw, gy );
				c.lineTo( gx, gy );
				c.closePath();
				c.fillStyle = g;
				c.fill();
			} );
		},

		weather( r, lt, t )
		{
			const P = P0;
			const sx = r.x + 200, sy = r.y + 200;
			c.save();
			c.translate( sx, sy );
			c.rotate( t * 0.6 );
			for ( let i = 0; i < 12; i++ )
			{
				c.rotate( TAU / 12 );
				L.fillR( -5, 95, 10, 34, 5, P.yellow, 0.9 );
			}
			c.restore();
			L.glow( P.yellow, 40, () => L.dot( sx, sy, 72 * E.outBack( seg( lt, 0, 0.3 ) ), P.yellow ) );
			L.txt( '18°', r.x + 400, r.y + 260, 200, P.text, { f: FD, w: 300, ls: -6 } );
			L.txt( 'Sunny  ·  feels like 17°', r.x + 410, r.y + 310, 26, P.muted, { f: FD } );
			const hours = [ [ 'Now', 18 ], [ '13', 19 ], [ '14', 21 ], [ '15', 22 ], [ '16', 21 ], [ '17', 19 ], [ '18', 17 ], [ '19', 15 ] ];
			const hw = ( r.w - 80 ) / hours.length, by = r.y + r.h - 70;
			c.beginPath();
			hours.forEach( ( [ , deg ], i ) =>
			{
				const x = r.x + 40 + hw * ( i + 0.5 ), y = by - 90 - ( deg - 15 ) * 14;
				if ( i === 0 )
					c.moveTo( x, y );
				else
					c.lineTo( x, y );
			} );
			c.strokeStyle = L.css( P.accent );
			c.lineWidth = 3;
			c.stroke();
			hours.forEach( ( [ h, deg ], i ) =>
			{
				const a = seg( lt, i * 0.03, i * 0.03 + 0.2 );
				const x = r.x + 40 + hw * ( i + 0.5 ), y = by - 90 - ( deg - 15 ) * 14;
				L.alpha( a, () =>
				{
					L.dot( x, y, 6, P.accent );
					L.txt( `${ deg }°`, x, y - 16, 18, P.text, { align: 'center', w: 600 } );
					L.icon( i > 5 ? 'cloud' : 'sun', x - 14, by - 40, 28, i > 5 ? P.muted : P.yellow );
					L.txt( h, x, by + 22, 15, P.muted, { align: 'center' } );
				} );
			} );
		},

		pixels( r, lt, t )
		{
			const P = P0;
			const mask = getPixelMask();
			const n = 24, cs = 22, gx = r.x + 60, gy = r.y + ( r.h - n * cs ) / 2;
			L.fillR( gx - 8, gy - 8, n * cs + 16, n * cs + 16, 8, P.canvas );
			for ( let i = 0; i <= n; i++ )
			{
				L.line( gx + i * cs, gy, gx + i * cs, gy + n * cs, P.border, 1, 0.6 );
				L.line( gx, gy + i * cs, gx + n * cs, gy + i * cs, P.border, 1, 0.6 );
			}
			const shown = Math.floor( mask.length * E.outCubic( seg( lt, 0, 0.42 ) ) );
			let last = null;
			for ( let i = 0; i < shown; i++ )
			{
				const m = mask[ i ];
				L.fillR( gx + m.x * cs + 1, gy + m.y * cs + 1, cs - 2, cs - 2, 2, m.thread ? P.accent : P.text );
				last = m;
			}
			if ( last && lt < 0.45 )
				L.pointer( gx + last.x * cs + cs / 2, gy + last.y * cs + cs / 2, 1.1 );
			const sw = [ P.text, P.accent, P.blue, P.success, P.magenta, P.yellow, P.error, P.cyan ];
			sw.forEach( ( col, i ) =>
			{
				const x = gx + n * cs + 80 + ( i % 2 ) * 76, y = gy + Math.floor( i / 2 ) * 76;
				L.fillR( x, y, 60, 60, 10, col );
				if ( i === 1 )
					L.strokeR( x - 4, y - 4, 68, 68, 13, P.text, 2 );
			} );
			L.txt( 'loom-icon.png  ·  24 × 24', gx + n * cs + 80, gy + 340, 16, P.muted, { f: FM } );
			L.txt( `${ shown } pixels`, gx + n * cs + 80, gy + 368, 16, P.muted, { f: FM } );
		},

		api( r, lt, t )
		{
			const P = P0;
			L.fillR( r.x + 20, r.y + 20, 76, 40, 8, P.accent, 0.2 );
			L.txt( 'GET', r.x + 58, r.y + 46, 16, P.accent, { f: FM, w: 700, align: 'center' } );
			L.fillR( r.x + 106, r.y + 20, r.w - 250, 40, 8, P.raised );
			L.txt( 'http://localhost:7777/api/chats', r.x + 122, r.y + 46, 16, P.text, { f: FM } );
			L.fillR( r.x + r.w - 134, r.y + 20, 114, 40, 8, P.accent );
			L.txt( 'Send', r.x + r.w - 77, r.y + 46, 16, P.canvas, { w: 700, align: 'center' } );
			const ok = seg( lt, 0.05, 0.15 );
			L.alpha( ok, () =>
			{
				L.fillR( r.x + 20, r.y + 84, 92, 30, 7, P.success, 0.18 );
				L.txt( '200 OK', r.x + 66, r.y + 104, 14, P.success, { f: FM, w: 700, align: 'center' } );
				L.txt( '42 ms  ·  1.2 KB', r.x + 128, r.y + 104, 14, P.muted, { f: FM } );
			} );
			const json = [
				[ [ '{', 'p' ] ],
				[ [ '  "chats"', 'k' ], [ ': [', 'p' ] ],
				[ [ '    { ', 'p' ], [ '"id"', 'k' ], [ ': ', 'p' ], [ '"c-142"', 's' ], [ ', ', 'p' ], [ '"title"', 'k' ], [ ': ', 'p' ], [ '"Car drift"', 's' ], [ ', ', 'p' ], [ '"state"', 'k' ], [ ': ', 'p' ], [ '"done"', 's' ], [ ' },', 'p' ] ],
				[ [ '    { ', 'p' ], [ '"id"', 'k' ], [ ': ', 'p' ], [ '"c-143"', 's' ], [ ', ', 'p' ], [ '"title"', 'k' ], [ ': ', 'p' ], [ '"Chase camera"', 's' ], [ ', ', 'p' ], [ '"state"', 'k' ], [ ': ', 'p' ], [ '"working"', 's' ], [ ' }', 'p' ] ],
				[ [ '  ],', 'p' ] ],
				[ [ '  "host"', 'k' ], [ ': ', 'p' ], [ '"this-pc"', 's' ], [ ',', 'p' ] ],
				[ [ '  "agents"', 'k' ], [ ': ', 'p' ], [ '2', 'n' ] ],
				[ [ '}', 'p' ] ],
			];
			const colors = { p: P.muted, k: P.blue, s: P.success, n: P.yellow };
			L.fillR( r.x + 20, r.y + 130, r.w - 40, r.h - 150, 10, P.term );
			json.forEach( ( ln, i ) =>
			{
				const a = seg( lt, 0.1 + i * 0.03, 0.2 + i * 0.03 );
				if ( a <= 0 )
					return;
				let x = r.x + 44 + ( 1 - a ) * 12;
				const y = r.y + 172 + i * 32;
				L.txt( String( i + 1 ), r.x + 36, y, 13, P.inactive, { f: FM, align: 'right' } );
				for ( const [ s, k ] of ln )
				{
					L.txt( s, x + 10, y, 16, colors[ k ], { f: FM, a } );
					x += L.measure( s, 16, 400, FM );
				}
			} );
		},

		notepad( r, lt, t )
		{
			const P = P0;
			const text = [ 'Ideas for modules', '', '- a pomodoro for focus blocks', '- a kanban board for each project', '- now playing in the title bar', '- a pixel editor for icons', '- your idea here' ];
			let budget = Math.floor( lt * 420 ) + 40;
			let caret = [ r.x + 60, r.y + 70 ];
			text.forEach( ( ln, i ) =>
			{
				const y = r.y + 70 + i * 44;
				L.txt( String( i + 1 ), r.x + 36, y, 14, P.inactive, { f: FM, align: 'right' } );
				if ( budget <= 0 )
					return;
				const s = ln.slice( 0, budget );
				L.txt( s, r.x + 60, y, i === 0 ? 30 : 24, i === 0 ? P.text : ln.endsWith( 'here' ) ? P.accent : P.muted, { w: i === 0 ? 700 : 400, f: i === 0 ? FD : FT } );
				caret = [ r.x + 62 + L.measure( s, i === 0 ? 30 : 24, i === 0 ? 700 : 400, i === 0 ? FD : FT ), y ];
				budget -= ln.length + 1;
			} );
			L.fillR( caret[ 0 ], caret[ 1 ] - 24, 2.5, 30, 0, P.accent );
		},

		yours( r, lt, t )
		{
			const P = P0;
			const pulse = 0.5 + 0.5 * Math.sin( t * 6 );
			c.save();
			c.setLineDash( [ 14, 10 ] );
			c.lineDashOffset = -t * 40;
			L.strokeR( r.x + 40, r.y + 40, r.w - 80, r.h - 80, 18, P.accent, 2.5 );
			c.restore();
			const cx = r.x + r.w / 2, cy = r.y + r.h / 2 - 50;
			const s = E.outBack( seg( lt, 0, 0.35 ) );
			L.scaled( s, cx, cy, () =>
			{
				L.glow( P.accent, 20 + pulse * 20, () => L.dot( cx, cy, 56, P.accent ) );
				L.icon( 'plus', cx - 30, cy - 30, 60, P.canvas );
			} );
			L.txt( 'Your module.', cx, cy + 140, 64, P.text, { f: FD, w: 700, align: 'center', a: seg( lt, 0.1, 0.3 ), ls: -1.5 } );
			L.txt( '%LOCALAPPDATA%\\Lokn\\modules\\<yours>', cx, cy + 186, 18, P.muted, { f: FM, align: 'center', a: seg( lt, 0.2, 0.4 ) } );
		},
	};

	function kanbanCard( x, y, w, title, col, P, lift )
	{
		L.shadow( lerp( 8, 34, lift ), lerp( 3, 16, lift ), lerp( 0.25, 0.5, lift ), () => L.fillR( x, y, w, 82, 10, P.raised ) );
		L.fillR( x, y + 12, 4, 58, 2, col );
		L.txt( title, x + 20, y + 34, 16, P.text, { w: 600 } );
		L.fillR( x + 20, y + 50, 64, 18, 5, col, 0.18 );
		L.dot( x + w - 26, y + 58, 11, P.hover );
	}

	function phoneScreen( t )
	{
		const P = P0, s = { x: 185, y: 360, w: 190, h: 400 };
		c.save();
		L.clipR( s.x, s.y, s.w, s.h, 26 );
		c.fillStyle = L.css( P.canvas );
		c.fillRect( s.x, s.y, s.w, s.h );
		L.txt( '9:41', s.x + 22, s.y + 20, 10, P.text, { w: 600 } );
		L.fillR( s.x + s.w - 40, s.y + 12, 18, 8, 2, P.text, 0.8 );
		L.logo( s.x + 24, s.y + 44, 16 );
		L.txt( 'Pricing page speed', s.x + 38, s.y + 48, 11.5, P.text, { w: 600 } );
		L.line( s.x, s.y + 62, s.x + s.w, s.y + 62, P.divider );
		let y = s.y + 74;
		const ub = L.wrap( 'Make the pricing page faster', 116, 10 );
		const ubh = ub.length * 14 + 12;
		L.fillR( s.x + s.w - 12 - 128, y, 128, ubh, 9, P.raised );
		ub.forEach( ( ln, i ) => L.txt( ln, s.x + s.w - 12 - 122, y + 18 + i * 14, 10, P.text ) );
		y += ubh + 10;
		const words = 'Pricing now loads in 0.9 s instead of 2.7 s. The report is open on your PC.'.split( ' ' );
		const shown = ( t - 44.4 ) * 16;
		const lines = L.wrap( words.join( ' ' ), 164, 10 );
		let wi = 0;
		lines.forEach( ( ln, li ) =>
		{
			let x = s.x + 12;
			for ( const w of ln.split( ' ' ) )
			{
				const a = clamp( shown - wi++ );
				if ( a > 0 )
					L.txt( w, x, y + 10 + li * 14, 10, P.text, { a } );
				x += L.measure( w + ' ', 10 );
			}
		} );
		y += lines.length * 14 + 8;
		L.alpha( seg( t, 45.4, 45.6 ), () =>
		{
			L.icon( 'chev', s.x + 8, y - 3, 12, P.muted );
			L.txt( 'Ran 2 commands, opened 2 panels', s.x + 22, y + 6, 9.5, P.muted );
		} );
		y += 18;
		L.alpha( seg( t, 45.6, 45.85 ), () =>
		{
			L.strokeR( s.x + 12.5, y + 0.5, s.w - 25, 62, 6, P.border );
			L.txt( 'Pricing.astro', s.x + 20, y + 15, 9, P.text, { w: 600 } );
			[ [ 1, 0.6 ], [ 1, 0.8 ], [ -1, 0.5 ] ].forEach( ( d, i ) =>
			{
				L.fillR( s.x + 13, y + 22 + i * 12, s.w - 26, 12, 0, d[ 0 ] > 0 ? P.success : P.error, 0.13 );
				L.fillR( s.x + 22, y + 26 + i * 12, ( s.w - 50 ) * d[ 1 ], 4, 2, d[ 0 ] > 0 ? P.success : P.error, 0.7 );
			} );
		} );
		y += 74;
		const rp = seg( t, 47.0, 47.25 );
		if ( rp > 0 )
			L.alpha( rp, () =>
			{
				L.fillR( s.x + s.w - 12 - 62, y + ( 1 - rp ) * 8, 62, 26, 9, P.accent );
				L.txt( 'Ship it.', s.x + s.w - 12 - 31, y + 17 + ( 1 - rp ) * 8, 10, P.canvas, { w: 600, align: 'center' } );
			} );
		L.fillR( s.x + 10, s.y + s.h - 44, s.w - 20, 30, 10, P.raised );
		L.txt( 'Message', s.x + 22, s.y + s.h - 25, 10, P.muted );
		L.dot( s.x + s.w - 26, s.y + s.h - 29, 9, P.accent );
		// Push notification
		const nIn = E.outBack( seg( t, 46.0, 46.4 ) ), nOut = E.inCubic( seg( t, 46.95, 47.2 ) );
		if ( nIn > 0 && nOut < 1 )
		{
			const ny = s.y + 8 + ( nIn - 1 ) * 90 - nOut * 90;
			L.shadow( 16, 6, 0.5, () => L.fillR( s.x + 8, ny, s.w - 16, 66, 14, P.raised ) );
			L.strokeR( s.x + 8.5, ny + 0.5, s.w - 17, 65, 14, P.border );
			L.logo( s.x + 24, ny + 18, 14 );
			L.txt( 'Pricing page speed', s.x + 38, ny + 22, 10.5, P.text, { w: 600 } );
			L.txt( 'Ready for review', s.x + 16, ny + 40, 9.5, P.muted );
			L.fillR( s.x + s.w - 64, ny + 42, 46, 18, 6, P.accent );
			L.txt( 'Reply', s.x + s.w - 41, ny + 55, 9.5, P.canvas, { w: 600, align: 'center' } );
			L.ripple( s.x + s.w - 41, ny + 51, t - 46.75, P.text, 26 );
		}
		c.restore();
	}

	/** Points the parts at the context they draw on. */
	function use( ctx )
	{
		c = ctx;
		L.use( ctx );
	}

	root.LoomParts = { use, pointAt, TERM_PRICING, pricingPage, reportPage, TILES, moduleTile, drawEditor, MONTAGE, MONTAGE_DRAW, phoneScreen };
} )( typeof window !== 'undefined' ? window : globalThis );
