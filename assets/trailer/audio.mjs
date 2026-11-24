// Synthesizes the trailer soundtrack to artifacts/trailer/loom-trailer.wav: a calm, warm electronic score in F major
// at 100 BPM (half-time drums, a pulsing filtered bass, wide soft pads, a muted arpeggio behind them) plus the
// sound design cues in timeline.js. The arrangement follows the scenes in timeline.js.
//   node assets/trailer/audio.mjs
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

const require = createRequire( import.meta.url );
const TL = require( './timeline.js' );
const here = path.dirname( fileURLToPath( import.meta.url ) );
const outFile = path.resolve( here, '..', '..', 'artifacts', 'trailer', 'loom-trailer.wav' );

const SR = 48000, D = TL.DURATION, N = SR * D, TAU = Math.PI * 2, BEAT = 60 / TL.BPM, STEP = BEAT / 4;
const L = new Float32Array( N ), R = new Float32Array( N );
const RvL = new Float32Array( N ), RvR = new Float32Array( N );
const DlL = new Float32Array( N ), DlR = new Float32Array( N );
const duck = new Float32Array( N );

/* ---------- helpers ---------- */

const mtof = m => 440 * 2 ** ( ( m - 69 ) / 12 );
const clamp = ( x, a, b ) => ( x < a ? a : x > b ? b : x );
const seg = ( t, a, b ) => clamp( ( t - a ) / ( b - a ), 0, 1 );
let seed = 1337;
const noise = () => { seed = ( seed * 1664525 + 1013904223 ) >>> 0; return seed / 2147483648 - 1; };

class Biquad
{
	constructor( type = 'lp', f = 1000, q = 0.707 ) { this.x1 = this.x2 = this.y1 = this.y2 = 0; this.set( type, f, q ); }
	set( type, f, q )
	{
		f = clamp( f, 20, SR * 0.45 );
		const w = TAU * f / SR, cs = Math.cos( w ), a = Math.sin( w ) / ( 2 * q );
		let b0, b1, b2;
		if ( type === 'lp' ) { b0 = ( 1 - cs ) / 2; b1 = 1 - cs; b2 = b0; }
		else if ( type === 'hp' ) { b0 = ( 1 + cs ) / 2; b1 = -( 1 + cs ); b2 = b0; }
		else { b0 = a; b1 = 0; b2 = -a; }
		const a0 = 1 + a;
		this.b0 = b0 / a0; this.b1 = b1 / a0; this.b2 = b2 / a0; this.a1 = -2 * cs / a0; this.a2 = ( 1 - a ) / a0;
	}
	run( x )
	{
		const y = this.b0 * x + this.b1 * this.x1 + this.b2 * this.x2 - this.a1 * this.y1 - this.a2 * this.y2;
		this.x2 = this.x1; this.x1 = x; this.y2 = this.y1; this.y1 = y;
		return y;
	}
}
function put( i, v, pan = 0, rev = 0, dly = 0 )
{
	if ( i < 0 || i >= N )
		return;
	const a = ( pan + 1 ) * Math.PI / 4, l = v * Math.cos( a ), r = v * Math.sin( a );
	L[ i ] += l; R[ i ] += r;
	if ( rev ) { RvL[ i ] += l * rev; RvR[ i ] += r * rev; }
	if ( dly ) { DlL[ i ] += l * dly; DlR[ i ] += r * dly; }
}
function blep( t, dt )
{
	if ( t < dt ) { t /= dt; return t + t - t * t - 1; }
	if ( t > 1 - dt ) { t = ( t - 1 ) / dt; return t * t + t + t + 1; }
	return 0;
}
const saw = ( ph, dt ) => 2 * ph - 1 - blep( ph, dt );
const has = ( t, ranges ) => ranges.some( ( [ a, b ] ) => t >= a && t < b );

/* ---------- harmony and arrangement (cut points of the picture) ---------- */

// Fmaj9 – Am7 – Dm9 – B♭maj7(♯11), two bars each.
const CHORDS = [
	{ pad: [ 53, 57, 60, 64, 67 ], bass: 41, arp: [ 65, 69, 72, 76, 79 ] },
	{ pad: [ 52, 57, 60, 64, 67 ], bass: 45, arp: [ 64, 67, 69, 72, 76 ] },
	{ pad: [ 50, 57, 60, 64, 65 ], bass: 38, arp: [ 62, 65, 69, 72, 76 ] },
	{ pad: [ 46, 53, 57, 62, 64 ], bass: 34, arp: [ 58, 62, 64, 69, 74 ] },
];
const FINAL = { pad: [ 41, 53, 57, 60, 64, 67, 72 ], bass: 41, arp: [ 72, 76, 79, 84 ] };
const S = TL.SCENES, LOGO_T = TL.LOGO;
const chordAt = t => ( t >= LOGO_T ? FINAL : CHORDS[ Math.floor( t / ( BEAT * 8 ) ) % 4 ] );

const DRUMS = [ [ S.app[ 0 ], S.app[ 1 ] - 0.2 ], [ S.workspace[ 0 ], S.workspace[ 1 ] - 0.2 ], [ S.modules[ 0 ], S.modules[ 1 ] - 0.2 ], [ S.phone[ 0 ], S.themes[ 1 ] - 0.4 ] ];
const HATS = [ [ S.app[ 0 ] + 1.2, S.app[ 1 ] - 0.2 ], [ S.workspace[ 0 ], S.themes[ 1 ] - 0.4 ] ];
const BASS = [ [ 2.4, S.end[ 0 ] ], [ LOGO_T, LOGO_T + 5.2 ] ];
const ARP = [ [ 0.6, D ] ];
/** The arpeggio climbs in octaves under the closing headline, into the logo. */
const RISE = [ S.end[ 0 ], LOGO_T ];

/** How open the filters are over time (0 dark … 1 open): brightest on the modules, softer at the edges. */
function bright( t )
{
	if ( t < 2.4 ) return 0.2 + 0.1 * t / 2.4;
	if ( t < S.app[ 0 ] ) return 0.35;
	if ( t < S.modules[ 0 ] ) return 0.45 + 0.15 * seg( t, S.app[ 0 ], S.modules[ 0 ] );
	if ( t < S.phone[ 0 ] ) return 0.6 + 0.15 * seg( t, S.modules[ 0 ], S.phone[ 0 ] );
	if ( t < S.end[ 0 ] ) return 0.6;
	if ( t < LOGO_T ) return 0.35 + 0.25 * seg( t, S.end[ 0 ], LOGO_T );
	return 0.45;
}

/* ---------- drums ---------- */

function kick( t0, gain )
{
	const i0 = Math.round( t0 * SR ), n = Math.round( 0.7 * SR ), lp = new Biquad( 'lp', 1800, 0.7 );
	let ph = 0;
	for ( let j = 0; j < n; j++ )
	{
		const t = j / SR;
		ph += TAU * ( 41 + 85 * Math.exp( -t * 28 ) ) / SR;
		let s = Math.sin( ph ) * Math.exp( -t * 5 );
		if ( j < 160 )
			s += noise() * 0.15 * ( 1 - j / 160 );
		put( i0 + j, lp.run( Math.tanh( s * 1.6 ) ) * gain, 0, 0.05 );
		if ( i0 + j < N )
			duck[ i0 + j ] = Math.max( duck[ i0 + j ], Math.exp( -t * 4.5 ) );
	}
}
/** Dark, roomy snare stand-in: band-passed noise with a low body, mostly reverb. */
function snare( t0, gain )
{
	const i0 = Math.round( t0 * SR ), n = Math.round( 0.6 * SR ), bp = new Biquad( 'bp', 900, 0.7 ), lp = new Biquad( 'lp', 3500, 0.7 );
	let ph = 0;
	for ( let j = 0; j < n; j++ )
	{
		const t = j / SR;
		ph += TAU * 180 / SR;
		const s = lp.run( bp.run( noise() ) ) * Math.exp( -t * 9 ) * 1.6 + Math.sin( ph ) * Math.exp( -t * 22 ) * 0.5;
		put( i0 + j, s * gain, 0.05, 0.8 );
	}
}
function hat( t0, gain, pan )
{
	const i0 = Math.round( t0 * SR ), n = Math.round( 0.045 * SR ), hp = new Biquad( 'hp', 7000, 0.7 ), lp = new Biquad( 'lp', 11000, 0.7 );
	for ( let j = 0; j < n; j++ )
		put( i0 + j, lp.run( hp.run( noise() ) ) * Math.exp( -j / SR * 85 ) * gain, pan, 0.1 );
}
function renderDrums()
{
	const steps = Math.floor( D / STEP );
	for ( let k = 0; k < steps; k++ )
	{
		const t = k * STEP, s = k % 16;
		if ( has( t, DRUMS ) )
		{
			if ( s === 0 || s === 10 )
				kick( t, 0.45 );
			if ( s === 8 )
				snare( t, 0.13 );
		}
		if ( has( t, HATS ) && s % 2 === 0 )
			hat( t + ( s % 4 === 2 ? STEP * 0.1 : 0 ), s % 4 === 2 ? 0.04 : 0.02, s % 4 === 2 ? 0.25 : -0.2 );
	}
}

/* ---------- tonal parts ---------- */

function renderPad()
{
	const bufL = new Float32Array( N ), bufR = new Float32Array( N );
	const lpL = new Biquad( 'lp', 600, 0.6 ), lpR = new Biquad( 'lp', 600, 0.6 );
	const phases = new Float64Array( 64 );
	let prev = null, change = 0;
	for ( let i = 0; i < N; i++ )
	{
		const t = i / SR;
		const ch = chordAt( t );
		if ( ch !== prev ) { prev = ch; change = t; }
		const notes = ch.pad;
		const xf = clamp( ( t - change ) / 0.6, 0, 1 );
		let l = 0, r = 0, k = 0;
		for ( let n = 0; n < notes.length; n++ )
			for ( const det of [ -7, 7 ] )
			{
				const f = mtof( notes[ n ] ) * 2 ** ( ( det + Math.sin( t * 0.3 + n ) * 2 ) / 1200 ), dt = f / SR;
				phases[ k ] = ( phases[ k ] + dt ) % 1;
				const s = saw( phases[ k ], dt ) * 0.6 + Math.sin( TAU * phases[ k ] ) * 0.4;
				if ( det < 0 ) l += s; else r += s;
				k++;
			}
		bufL[ i ] = l / notes.length * ( 0.55 + 0.45 * xf );
		bufR[ i ] = r / notes.length * ( 0.55 + 0.45 * xf );
	}
	for ( let i = 0; i < N; i++ )
	{
		const t = i / SR;
		if ( ( i & 63 ) === 0 )
		{
			const f = 220 + 1900 * bright( t ) ** 1.5 + 150 * Math.sin( t * 0.45 );
			lpL.set( 'lp', f, 0.6 );
			lpR.set( 'lp', f, 0.6 );
		}
		const swell = t < 2.55 ? seg( t, 0, 2.4 ) ** 1.5 : 1;
		const tail = t > 57 ? Math.max( 0, 1 - ( t - 57 ) / 3 ) : 1;
		const g = 0.26 * swell * tail * ( 1 - 0.35 * duck[ i ] );
		const l = lpL.run( bufL[ i ] ) * g, r = lpR.run( bufR[ i ] ) * g;
		L[ i ] += l; R[ i ] += r;
		RvL[ i ] += l * 0.55; RvR[ i ] += r * 0.55;
	}
}

function renderDrone()
{
	let a = 0, b = 0;
	for ( let i = 0; i < N; i++ )
	{
		const t = i / SR;
		const g = 0.12 * seg( t, 0, 1.5 ) * ( 1 - seg( t, 2.4, 4.4 ) ) + 0.11 * seg( t, S.end[ 0 ], S.end[ 0 ] + 1.4 ) * ( 1 - seg( t, D - 2, D ) );
		if ( g <= 0 )
			continue;
		a += TAU * mtof( 26 ) / SR;
		b += TAU * mtof( 33 ) * 1.0015 / SR;
		const v = ( Math.sin( a ) + 0.45 * Math.sin( b ) + 0.12 * Math.sin( 3 * a ) ) * g;
		L[ i ] += v; R[ i ] += v;
		RvL[ i ] += v * 0.15; RvR[ i ] += v * 0.15;
	}
}

/** Pulsing 8th-note bass ostinato, filtered and ducked by the kick. Sustained in the breakdown. */
function renderBass()
{
	const lp = new Biquad( 'lp', 200, 1 );
	let ph = 0, ph2 = 0.5, sub = 0, f = mtof( 38 );
	for ( let i = 0; i < N; i++ )
	{
		const t = i / SR;
		if ( !has( t, BASS ) )
		{
			lp.run( 0 );
			continue;
		}
		const k8 = Math.floor( t / ( BEAT / 2 ) ), st = t - k8 * BEAT / 2;
		const sustained = t >= LOGO_T;
		const env = sustained ? 0.8 : 0.25 + 0.75 * Math.exp( -st * 9 );
		const oct = !sustained && k8 % 8 === 7 ? 12 : 0;
		f += ( mtof( chordAt( t ).bass + oct ) - f ) * 0.01;
		ph = ( ph + f / SR ) % 1;
		ph2 = ( ph2 + f * 1.004 / SR ) % 1;
		sub += TAU * f / 2 / SR;
		if ( ( i & 31 ) === 0 )
			lp.set( 'lp', 110 + 900 * bright( t ) * env, 1.1 );
		const tone = lp.run( saw( ph, f / SR ) + saw( ph2, f / SR ) ) * 0.45;
		const fade = t >= LOGO_T ? Math.max( 0, 1 - ( t - LOGO_T ) / 2.8 ) : 1;
		const v = ( tone + Math.sin( sub ) * 0.7 ) * env * 0.22 * ( 1 - 0.55 * duck[ i ] ) * fade;
		L[ i ] += v; R[ i ] += v;
	}
}

/** Muted arpeggio: sine + soft triangle through a closed filter, sitting behind the pads with a long echo. */
function pluck( t0, midi, gain, pan, cut )
{
	const i0 = Math.round( t0 * SR ), n = Math.round( 0.9 * SR ), lp = new Biquad( 'lp', cut, 0.8 ), f = mtof( midi );
	let ph = 0;
	for ( let j = 0; j < n; j++ )
	{
		const t = j / SR;
		ph = ( ph + f / SR ) % 1;
		const tri = 1 - 4 * Math.abs( ph - 0.5 );
		put( i0 + j, lp.run( Math.sin( TAU * ph ) * 0.7 + tri * 0.3 ) * Math.exp( -t * 5 ) * gain * Math.min( 1, j / 90 ), pan, 0.4, 0.5 );
	}
}
function renderArp()
{
	const ORDER = [ 0, 2, 1, 3, 2, 4, 3, 1 ];
	const steps = Math.floor( D / ( BEAT / 2 ) );
	for ( let k = 0; k < steps; k++ )
	{
		const t = k * BEAT / 2;
		if ( !has( t, ARP ) )
			continue;
		const ch = chordAt( t );
		const rising = t >= RISE[ 0 ] && t < RISE[ 1 ];
		const midi = rising ? ch.arp[ k % ch.arp.length ] + ( Math.floor( ( t - RISE[ 0 ] ) / 1.2 ) % 2 ) * 12 : ch.arp[ ORDER[ k % 8 ] % ch.arp.length ];
		const gain = t < 0.6 ? 0 : rising ? 0.035 + 0.02 * seg( t, RISE[ 0 ], RISE[ 1 ] ) : t >= LOGO_T ? 0.03 * Math.max( 0, 1 - ( t - LOGO_T ) / 3 ) : 0.04;
		pluck( t, midi, gain * ( k % 2 ? 0.75 : 1 ), k % 2 ? 0.5 : -0.5, 700 + 1800 * bright( t ) );
	}
}

/* ---------- sound design ---------- */

function sweep( t0, dur, f0, f1, gain, shape, pan0 = 0, pan1 = 0, rev = 0.4, q = 0.9 )
{
	const i0 = Math.round( t0 * SR ), n = Math.round( dur * SR ), bp = new Biquad( 'bp', f0, q ), lp = new Biquad( 'lp', 6000, 0.7 );
	for ( let j = 0; j < n; j++ )
	{
		const x = j / n;
		if ( ( j & 31 ) === 0 )
			bp.set( 'bp', f0 * ( f1 / f0 ) ** ( shape === 'hump' ? Math.sin( Math.PI * x * 0.5 ) : x ), q );
		const env = shape === 'rise' ? x ** 3 : Math.sin( Math.PI * x ) ** 2;
		put( i0 + j, lp.run( bp.run( noise() ) ) * env * gain, pan0 + ( pan1 - pan0 ) * x, rev );
	}
}
function tone( t0, dur, f0, f1, gain, decay, pan = 0, rev = 0.2 )
{
	const i0 = Math.round( t0 * SR ), n = Math.round( dur * SR );
	let ph = 0;
	for ( let j = 0; j < n; j++ )
	{
		const x = j / n, t = j / SR;
		ph += TAU * ( f0 * ( f1 / f0 ) ** x ) / SR;
		put( i0 + j, Math.sin( ph ) * Math.exp( -t * decay ) * gain * Math.min( 1, j / 80 ), pan, rev );
	}
}
function burst( t0, dur, type, f, gain, decay, pan = 0, rev = 0.2 )
{
	const i0 = Math.round( t0 * SR ), n = Math.round( dur * SR ), b = new Biquad( type, f, 0.8 );
	for ( let j = 0; j < n; j++ )
		put( i0 + j, b.run( noise() ) * Math.exp( -j / SR * decay ) * gain, pan, rev );
}

const CUES = {
	swell: c => { sweep( c.t - 1.4, 1.4, 300, 2600, 0.14 * c.gain, 'rise', -0.3, 0.3, 0.6, 1.1 ); tone( c.t, 2.4, 60, 38, 0.28 * c.gain, 1.6, 0, 0.4 ); },
	whoosh: c => sweep( c.t - c.dur * 0.55, c.dur, 250, 1800, 0.26 * c.gain, 'hump', -0.6, 0.6, 0.5, 0.8 ),
	swish: c => sweep( c.t - 0.12, 0.3, 600, 2600, 0.16 * c.gain, 'hump', -0.4, 0.4, 0.3, 0.9 ),
	sub: c => tone( c.t, 0.6, 72, 44, 0.32 * c.gain, 7, 0, 0.15 ),
	click: c => { burst( c.t, 0.018, 'bp', 1800, 0.09 * c.gain, 300, 0, 0.08 ); burst( c.t, 0.03, 'lp', 400, 0.08 * c.gain, 160, 0, 0 ); },
	type: c => burst( c.t, 0.016, 'bp', 1500 + noise() * 400, 0.055 * c.gain, 260, noise() * 0.25, 0.04 ),
};
function renderCues()
{
	for ( const c of TL.sfx )
	{
		const fn = CUES[ c.type ];
		if ( !fn )
			throw new Error( 'no sound for cue ' + c.type );
		fn( { t: c.t, dur: c.dur || 0.5, gain: c.gain ?? 1 } );
	}
}

/* ---------- delay, reverb, master ---------- */

function pingPong()
{
	const d = Math.round( BEAT * 0.75 * SR ), fb = 0.45;
	const bl = new Float32Array( d ), br = new Float32Array( d ), lpL = new Biquad( 'lp', 2200, 0.7 ), lpR = new Biquad( 'lp', 2200, 0.7 );
	let p = 0;
	for ( let i = 0; i < N; i++ )
	{
		const ol = bl[ p ], or = br[ p ];
		bl[ p ] = lpL.run( DlL[ i ] + DlR[ i ] + or * fb );
		br[ p ] = lpR.run( ol * fb );
		p = ( p + 1 ) % d;
		L[ i ] += ol * 0.5; R[ i ] += or * 0.5;
		RvL[ i ] += ol * 0.3; RvR[ i ] += or * 0.3;
	}
}
function reverb( input, offset )
{
	const out = new Float32Array( N ), pre = Math.round( 0.035 * SR );
	const combs = [ 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 ].map( l => ( { buf: new Float32Array( l + offset ), i: 0, s: 0 } ) );
	const aps = [ 556, 441, 341, 225 ].map( l => ( { buf: new Float32Array( l + offset ), i: 0 } ) );
	for ( let n = 0; n < N; n++ )
	{
		const x = n >= pre ? input[ n - pre ] * 0.12 : 0;
		let y = 0;
		for ( const c of combs )
		{
			const o = c.buf[ c.i ];
			c.s = o * 0.62 + c.s * 0.38;
			c.buf[ c.i ] = x + c.s * 0.9;
			c.i = ( c.i + 1 ) % c.buf.length;
			y += o;
		}
		for ( const a of aps )
		{
			const o = a.buf[ a.i ];
			a.buf[ a.i ] = y + o * 0.5;
			a.i = ( a.i + 1 ) % a.buf.length;
			y = o - y * 0.5;
		}
		out[ n ] = y;
	}
	return out;
}
function writeWav( file, l, r )
{
	const data = Buffer.alloc( N * 4 );
	for ( let i = 0; i < N; i++ )
	{
		data.writeInt16LE( Math.round( clamp( l[ i ], -1, 1 ) * 32767 ), i * 4 );
		data.writeInt16LE( Math.round( clamp( r[ i ], -1, 1 ) * 32767 ), i * 4 + 2 );
	}
	const h = Buffer.alloc( 44 );
	h.write( 'RIFF', 0 ); h.writeUInt32LE( 36 + data.length, 4 ); h.write( 'WAVE', 8 );
	h.write( 'fmt ', 12 ); h.writeUInt32LE( 16, 16 ); h.writeUInt16LE( 1, 20 ); h.writeUInt16LE( 2, 22 );
	h.writeUInt32LE( SR, 24 ); h.writeUInt32LE( SR * 4, 28 ); h.writeUInt16LE( 4, 32 ); h.writeUInt16LE( 16, 34 );
	h.write( 'data', 36 ); h.writeUInt32LE( data.length, 40 );
	fs.mkdirSync( path.dirname( file ), { recursive: true } );
	fs.writeFileSync( file, Buffer.concat( [ h, data ] ) );
}

const started = Date.now();
renderDrums();
renderDrone();
renderPad();
renderBass();
renderArp();
renderCues();
pingPong();
const wl = reverb( RvL, 0 ), wr = reverb( RvR, 23 );
let peak = 0;
for ( let i = 0; i < N; i++ )
{
	const t = i / SR;
	const fade = Math.min( 1, t / 0.3 ) * ( t > D - 1.8 ? Math.max( 0, 1 - ( t - ( D - 1.8 ) ) / 1.8 ) ** 1.4 : 1 );
	L[ i ] = Math.tanh( ( L[ i ] + wl[ i ] ) * 1.1 ) * fade;
	R[ i ] = Math.tanh( ( R[ i ] + wr[ i ] ) * 1.1 ) * fade;
	peak = Math.max( peak, Math.abs( L[ i ] ), Math.abs( R[ i ] ) );
}
const norm = 0.89 / peak;
for ( let i = 0; i < N; i++ )
{
	L[ i ] *= norm;
	R[ i ] *= norm;
}
writeWav( outFile, L, R );
console.log( `AUDIO ${ outFile } (${ ( ( Date.now() - started ) / 1000 ).toFixed( 1 ) } s, peak ${ peak.toFixed( 2 ) })` );
