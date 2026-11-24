// Drawing kit for the Loom trailer: math, color, text, shapes, icons, the logo and Loom's UI pieces.
// Everything draws on the context set with LK.use( ctx ) and depends only on its arguments, so a frame
// is a pure function of time.
( function ( root )
{
	'use strict';

	const W = 1920, H = 1080, TAU = Math.PI * 2;
	const FD = '"Segoe UI Variable Display", "Segoe UI", sans-serif';
	const FT = '"Segoe UI Variable Text", "Segoe UI", sans-serif';
	const FM = '"Cascadia Code", "Cascadia Mono", Consolas, monospace';

	/* ---------- math ---------- */

	const clamp = ( x, a = 0, b = 1 ) => ( x < a ? a : x > b ? b : x );
	const seg = ( t, a, b ) => clamp( ( t - a ) / ( b - a ) );
	const lerp = ( a, b, t ) => a + ( b - a ) * t;
	const frac = x => x - Math.floor( x );
	const E = {
		lin: t => t,
		inCubic: t => t * t * t,
		outCubic: t => 1 - ( 1 - t ) ** 3,
		inOutCubic: t => ( t < 0.5 ? 4 * t * t * t : 1 - ( -2 * t + 2 ) ** 3 / 2 ),
		outQuint: t => 1 - ( 1 - t ) ** 5,
		inQuint: t => t ** 5,
		outExpo: t => ( t >= 1 ? 1 : 1 - 2 ** ( -10 * t ) ),
		inExpo: t => ( t <= 0 ? 0 : 2 ** ( 10 * t - 10 ) ),
		inOutExpo: t => ( t <= 0 ? 0 : t >= 1 ? 1 : t < 0.5 ? 2 ** ( 20 * t - 10 ) / 2 : ( 2 - 2 ** ( -20 * t + 10 ) ) / 2 ),
		outBack: t => { const a = 1.70158, b = a + 1; return 1 + b * ( t - 1 ) ** 3 + a * ( t - 1 ) ** 2; },
		inOutSine: t => -( Math.cos( Math.PI * t ) - 1 ) / 2,
	};
	/** Damped spring from 0 to 1; dt in seconds since the move started. */
	const spring = ( dt, f = 1.2, d = 7.5 ) => ( dt <= 0 ? 0 : 1 - Math.exp( -d * dt ) * Math.cos( TAU * f * dt ) );

	function hash( n )
	{
		n = ( n ^ 61 ) ^ ( n >>> 16 );
		n = ( n + ( n << 3 ) ) | 0;
		n ^= n >>> 4;
		n = Math.imul( n, 0x27d4eb2d );
		n ^= n >>> 15;
		return ( n >>> 0 ) / 4294967296;
	}
	const rnd = ( i, j = 0 ) => hash( ( i * 7919 + j * 104729 + 1013 ) | 0 );

	/* ---------- color ---------- */

	const hexCache = new Map();
	function hex( h )
	{
		let v = hexCache.get( h );
		if ( !v )
		{
			const n = parseInt( h.slice( 1 ), 16 );
			v = [ ( n >> 16 ) & 255, ( n >> 8 ) & 255, n & 255 ];
			hexCache.set( h, v );
		}
		return v;
	}
	const arr = v => ( typeof v === 'string' ? hex( v ) : v );
	function css( v, a = 1 )
	{
		if ( typeof v === 'string' && a >= 1 )
			return v;
		const x = arr( v );
		return `rgba(${ x[ 0 ] | 0 },${ x[ 1 ] | 0 },${ x[ 2 ] | 0 },${ a })`;
	}
	function mix( a, b, t )
	{
		const A = arr( a ), B = arr( b );
		return [ lerp( A[ 0 ], B[ 0 ], t ), lerp( A[ 1 ], B[ 1 ], t ), lerp( A[ 2 ], B[ 2 ], t ) ];
	}
	function pal( o )
	{
		const p = {};
		for ( const k in o )
			p[ k ] = hex( o[ k ] );
		return p;
	}
	function mixPal( a, b, t )
	{
		const p = {};
		for ( const k in a )
			p[ k ] = mix( a[ k ], b[ k ], t );
		return p;
	}

	/** Loom's own theme (Core/Theme.cs defaults). */
	const LOOM = pal( {
		canvas: '#262624', panel: '#222220', raised: '#2d2d29', hover: '#3a3a34', text: '#e8e6df', muted: '#a09f96',
		border: '#3b3b35', accent: '#d5a583', divider: '#353531', inactive: '#85857d', success: '#9bcb9a', error: '#e6a09a',
		attention: '#8fb4d9', yellow: '#dfc07f', magenta: '#c59bd0', cyan: '#80c2bd', blue: '#83a8d4', drop: '#655343', term: '#1d1d1b',
	} );
	/** Themes the themes beat cycles through, in Loom's palette keys: Loom, then Tern catalog themes. */
	const THEMES = [
		LOOM,
		pal( {
			canvas: '#f2efe7', panel: '#f9f7f1', raised: '#fefdfb', hover: '#ede9df', text: '#3a3832', muted: '#706b61',
			border: '#c8c4b8', accent: '#557350', divider: '#dcd7cb', inactive: '#7d8c77', success: '#4f6e47', error: '#a34a41',
			attention: '#86692a', yellow: '#8f7226', magenta: '#865878', cyan: '#3a7678', blue: '#466888', drop: '#dbe3d6', term: '#f6f3ec',
		} ),
		pal( {
			canvas: '#1a1b26', panel: '#16161e', raised: '#24283b', hover: '#2a2f41', text: '#c0caf5', muted: '#7a83ad',
			border: '#2f344d', accent: '#bb9af7', divider: '#232535', inactive: '#51597d', success: '#9ece6a', error: '#f7768e',
			attention: '#7dcfff', yellow: '#e0af68', magenta: '#bb9af7', cyan: '#7dcfff', blue: '#7aa2f7', drop: '#3d3560', term: '#13131b',
		} ),
		pal( {
			canvas: '#eff1f5', panel: '#e6e9ef', raised: '#dce0e8', hover: '#ccd0da', text: '#4c4f69', muted: '#7c7f93',
			border: '#ccd0da', accent: '#8839ef', divider: '#d5d9e2', inactive: '#9ca0b0', success: '#40a02b', error: '#d20f39',
			attention: '#1e66f5', yellow: '#df8e1d', magenta: '#ea76cb', cyan: '#179299', blue: '#1e66f5', drop: '#cbb8f0', term: '#dce0e8',
		} ),
		pal( {
			canvas: '#282828', panel: '#1d2021', raised: '#32302f', hover: '#3c3836', text: '#ebdbb2', muted: '#a89984',
			border: '#3c3836', accent: '#fe8019', divider: '#32302f', inactive: '#7c6f64', success: '#b8bb26', error: '#fb4934',
			attention: '#83a598', yellow: '#fabd2f', magenta: '#d3869b', cyan: '#8ec07c', blue: '#83a598', drop: '#5a3a1a', term: '#171a1a',
		} ),
		pal( {
			canvas: '#191724', panel: '#1f1d2e', raised: '#26233a', hover: '#2a2837', text: '#e0def4', muted: '#908caa',
			border: '#403d52', accent: '#ebbcba', divider: '#26233a', inactive: '#6e6a86', success: '#9ccfd8', error: '#eb6f92',
			attention: '#9ccfd8', yellow: '#f6c177', magenta: '#c4a7e7', cyan: '#9ccfd8', blue: '#31748f', drop: '#4a3a50', term: '#15131f',
		} ),
	];
	/** The logo thread's copper, from the Loom site. */
	const BRAND = { copper: '#c98b5f' };

	/* ---------- context and primitives ---------- */

	let c = null;
	const use = ctx => { c = ctx; };
	const ctx = () => c;

	function setFont( size, weight, fam, ls )
	{
		c.font = `${ weight || 400 } ${ size }px ${ fam || FT }`;
		c.letterSpacing = ( ls || 0 ) + 'px';
	}
	function measure( s, size, weight, fam, ls )
	{
		setFont( size, weight, fam, ls );
		const w = c.measureText( s ).width;
		c.letterSpacing = '0px';
		return w;
	}
	function txt( s, x, y, size, color, o )
	{
		o = o || {};
		setFont( size, o.w, o.f, o.ls );
		c.fillStyle = css( color, o.a ?? 1 );
		c.textAlign = o.align || 'left';
		c.textBaseline = o.base || 'alphabetic';
		c.fillText( s, x, y );
		c.letterSpacing = '0px';
	}
	const wrapCache = new Map();
	function wrap( s, maxW, size, weight, fam )
	{
		const key = `${ s }|${ maxW | 0 }|${ size }|${ weight }|${ fam }`;
		let lines = wrapCache.get( key );
		if ( lines )
			return lines;
		setFont( size, weight, fam );
		lines = [];
		let line = '';
		for ( const word of s.split( ' ' ) )
		{
			const next = line ? line + ' ' + word : word;
			if ( line && c.measureText( next ).width > maxW )
			{
				lines.push( line );
				line = word;
			}
			else
				line = next;
		}
		if ( line )
			lines.push( line );
		wrapCache.set( key, lines );
		return lines;
	}

	function rrect( x, y, w, h, r )
	{
		w = Math.max( 0, w );
		h = Math.max( 0, h );
		c.beginPath();
		c.roundRect( x, y, w, h, Math.max( 0, Math.min( r, w / 2, h / 2 ) ) );
	}
	function fillR( x, y, w, h, r, color, a )
	{
		rrect( x, y, w, h, r );
		c.fillStyle = css( color, a ?? 1 );
		c.fill();
	}
	function strokeR( x, y, w, h, r, color, lw, a )
	{
		rrect( x, y, w, h, r );
		c.strokeStyle = css( color, a ?? 1 );
		c.lineWidth = lw || 1;
		c.stroke();
	}
	function line( x1, y1, x2, y2, color, lw, a )
	{
		c.beginPath();
		c.moveTo( x1, y1 );
		c.lineTo( x2, y2 );
		c.strokeStyle = css( color, a ?? 1 );
		c.lineWidth = lw || 1;
		c.stroke();
	}
	function dot( x, y, r, color, a )
	{
		c.beginPath();
		c.arc( x, y, Math.max( 0, r ), 0, TAU );
		c.fillStyle = css( color, a ?? 1 );
		c.fill();
	}
	function ring( x, y, r, color, lw, a )
	{
		c.beginPath();
		c.arc( x, y, Math.max( 0, r ), 0, TAU );
		c.strokeStyle = css( color, a ?? 1 );
		c.lineWidth = lw || 1;
		c.stroke();
	}
	function clipR( x, y, w, h, r )
	{
		rrect( x, y, w, h, r || 0 );
		c.clip();
	}
	function alpha( a, fn )
	{
		if ( a <= 0.002 )
			return;
		c.save();
		c.globalAlpha *= Math.min( 1, a );
		fn();
		c.restore();
	}
	/** Runs fn scaled by s around (cx, cy). */
	function scaled( s, cx, cy, fn )
	{
		c.save();
		c.translate( cx, cy );
		c.scale( s, s );
		c.translate( -cx, -cy );
		fn();
		c.restore();
	}
	function glow( color, blur, fn )
	{
		c.save();
		c.shadowColor = css( color, 1 );
		c.shadowBlur = blur;
		fn();
		c.restore();
	}
	/** Soft drop shadow, warm and light enough for a paper background. */
	function shadow( blur, dy, a, fn )
	{
		c.save();
		c.shadowColor = `rgba(70,52,34,${ a * 0.45 })`;
		c.shadowBlur = blur;
		c.shadowOffsetY = dy;
		fn();
		c.restore();
	}

	/* ---------- polylines and threads ---------- */

	function bez( p0, p1, p2, p3, n )
	{
		const pts = [];
		for ( let i = 0; i <= n; i++ )
		{
			const t = i / n, u = 1 - t;
			const a = u * u * u, b = 3 * u * u * t, d = 3 * u * t * t, e = t * t * t;
			pts.push( [ a * p0[ 0 ] + b * p1[ 0 ] + d * p2[ 0 ] + e * p3[ 0 ], a * p0[ 1 ] + b * p1[ 1 ] + d * p2[ 1 ] + e * p3[ 1 ] ] );
		}
		return pts;
	}

	class Poly
	{
		constructor( pts )
		{
			this.p = pts;
			this.L = [ 0 ];
			for ( let i = 1; i < pts.length; i++ )
				this.L.push( this.L[ i - 1 ] + Math.hypot( pts[ i ][ 0 ] - pts[ i - 1 ][ 0 ], pts[ i ][ 1 ] - pts[ i - 1 ][ 1 ] ) );
			this.len = this.L[ this.L.length - 1 ];
		}
		at( s )
		{
			const L = this.L, p = this.p;
			s = clamp( s, 0, this.len );
			let lo = 0, hi = L.length - 1;
			while ( hi - lo > 1 )
			{
				const m = ( lo + hi ) >> 1;
				if ( L[ m ] <= s )
					lo = m;
				else
					hi = m;
			}
			const k = L[ hi ] > L[ lo ] ? ( s - L[ lo ] ) / ( L[ hi ] - L[ lo ] ) : 0;
			return [ lerp( p[ lo ][ 0 ], p[ hi ][ 0 ], k ), lerp( p[ lo ][ 1 ], p[ hi ][ 1 ], k ), Math.atan2( p[ hi ][ 1 ] - p[ lo ][ 1 ], p[ hi ][ 0 ] - p[ lo ][ 0 ] ) ];
		}
		trace( s0, s1 )
		{
			const a = this.at( s0 ), b = this.at( s1 );
			c.beginPath();
			c.moveTo( a[ 0 ], a[ 1 ] );
			for ( let i = 0; i < this.p.length; i++ )
				if ( this.L[ i ] > s0 && this.L[ i ] < s1 )
					c.lineTo( this.p[ i ][ 0 ], this.p[ i ][ 1 ] );
			c.lineTo( b[ 0 ], b[ 1 ] );
		}
		stroke( s0, s1, lw, color, a, glowBlur )
		{
			if ( s1 - s0 < 0.01 )
				return;
			c.save();
			this.trace( s0, s1 );
			c.lineWidth = lw;
			c.lineCap = 'round';
			c.lineJoin = 'round';
			c.strokeStyle = css( color, a ?? 1 );
			if ( glowBlur )
			{
				c.shadowColor = css( color, 1 );
				c.shadowBlur = glowBlur;
			}
			c.stroke();
			c.restore();
		}
	}

	/* ---------- the logo ---------- */

	const LOGO_PTS = bez( [ 8, 36 ], [ 16, 22 ], [ 24, 46 ], [ 32, 32 ], 48 ).concat( bez( [ 32, 32 ], [ 40, 18 ], [ 48, 18 ], [ 56, 28 ], 48 ).slice( 1 ) );
	const LOGO_POLY = new Poly( LOGO_PTS );
	let logoLayer = null;

	/**
	 * Draws the Loom mark centered at (cx, cy). o.circle (0..1) grows the ring from the left,
	 * o.s0/o.s1 (0..1) trim the thread, o.glow adds bloom.
	 * The ring is drawn on a device-space layer and the thread's 14-unit round-capped stroke is cut out of it,
	 * exactly like the mask in the brand SVG; the layer follows the current transform, so it stays sharp at any zoom.
	 */
	function logo( cx, cy, size, o )
	{
		o = o || {};
		const circle = o.circle ?? 1, s0 = o.s0 ?? 0, s1 = o.s1 ?? 1;
		const k = size / 64;
		if ( circle > 0.001 )
		{
			const m = c.getTransform(), cw = c.canvas.width, ch = c.canvas.height, h = size / 2;
			const xs = [], ys = [];
			for ( const [ x, y ] of [ [ cx - h, cy - h ], [ cx + h, cy - h ], [ cx - h, cy + h ], [ cx + h, cy + h ] ] )
			{
				xs.push( m.a * x + m.c * y + m.e );
				ys.push( m.b * x + m.d * y + m.f );
			}
			const x0 = Math.max( 0, Math.floor( Math.min( ...xs ) ) - 4 ), x1 = Math.min( cw, Math.ceil( Math.max( ...xs ) ) + 4 );
			const y0 = Math.max( 0, Math.floor( Math.min( ...ys ) ) - 4 ), y1 = Math.min( ch, Math.ceil( Math.max( ...ys ) ) + 4 );
			if ( x1 > x0 && y1 > y0 )
			{
				if ( !logoLayer || logoLayer.width < cw || logoLayer.height < ch )
				{
					logoLayer = document.createElement( 'canvas' );
					logoLayer.width = Math.max( cw, logoLayer ? logoLayer.width : 0 );
					logoLayer.height = Math.max( ch, logoLayer ? logoLayer.height : 0 );
				}
				const g = logoLayer.getContext( '2d' );
				g.setTransform( 1, 0, 0, 1, 0, 0 );
				g.clearRect( x0, y0, x1 - x0, y1 - y0 );
				g.save();
				g.beginPath();
				g.rect( x0, y0, x1 - x0, y1 - y0 );
				g.clip();
				g.setTransform( m );
				g.translate( cx - 32 * k, cy - 32 * k );
				g.scale( k, k );
				const a = Math.PI * Math.min( 1, circle ) + ( circle >= 1 ? 0.01 : 0 );
				g.beginPath();
				g.arc( 32, 32, 20, Math.PI - a, Math.PI + a );
				g.lineWidth = 6;
				g.lineCap = 'round';
				g.strokeStyle = css( o.ring || LOOM.text );
				g.stroke();
				g.globalCompositeOperation = 'destination-out';
				g.beginPath();
				LOGO_PTS.forEach( ( p, i ) => ( i ? g.lineTo( p[ 0 ], p[ 1 ] ) : g.moveTo( p[ 0 ], p[ 1 ] ) ) );
				g.lineWidth = 14;
				g.lineCap = 'round';
				g.lineJoin = 'round';
				g.strokeStyle = '#000';
				g.stroke();
				g.restore();
				c.save();
				c.setTransform( 1, 0, 0, 1, 0, 0 );
				c.globalAlpha *= o.a ?? 1;
				if ( o.glow )
				{
					c.shadowColor = css( o.ring || LOOM.text, 0.6 );
					c.shadowBlur = o.glow;
				}
				c.drawImage( logoLayer, x0, y0, x1 - x0, y1 - y0, x0, y0, x1 - x0, y1 - y0 );
				c.restore();
			}
		}
		if ( s1 > s0 )
		{
			c.save();
			c.translate( cx - 32 * k, cy - 32 * k );
			c.scale( k, k );
			LOGO_POLY.stroke( s0 * LOGO_POLY.len, s1 * LOGO_POLY.len, o.threadWidth || 6, o.thread || BRAND.copper, o.a ?? 1, o.glow );
			c.restore();
		}
	}

	/* ---------- easing ---------- */

	/** CSS cubic-bezier easing. */
	function cubicBezier( x1, y1, x2, y2 )
	{
		const cx = 3 * x1, bx = 3 * ( x2 - x1 ) - cx, ax = 1 - cx - bx;
		const cy = 3 * y1, by = 3 * ( y2 - y1 ) - cy, ay = 1 - cy - by;
		const sx = u => ( ( ax * u + bx ) * u + cx ) * u, sy = u => ( ( ay * u + by ) * u + cy ) * u, dx = u => ( 3 * ax * u + 2 * bx ) * u + cx;
		return x =>
		{
			if ( x <= 0 )
				return 0;
			if ( x >= 1 )
				return 1;
			let u = x;
			for ( let i = 0; i < 8; i++ )
			{
				const d = sx( u ) - x, s = dx( u );
				if ( Math.abs( d ) < 1e-6 || Math.abs( s ) < 1e-6 )
					break;
				u = clamp( u - d / s );
			}
			return sy( u );
		};
	}
	/** The Loom site's easing: cubic-bezier( 0.2, 0.7, 0.1, 1 ). */
	const SITE = cubicBezier( 0.2, 0.7, 0.1, 1 );

	/* ---------- pointer and ripples ---------- */

	function pointer( x, y, s = 1, a = 1 )
	{
		if ( a <= 0 )
			return;
		c.save();
		c.globalAlpha *= a;
		c.translate( x, y );
		c.scale( s, s );
		c.beginPath();
		c.moveTo( 0, 0 );
		c.lineTo( 0, 23 );
		c.lineTo( 5.6, 17.6 );
		c.lineTo( 9.6, 26.4 );
		c.lineTo( 13.4, 24.8 );
		c.lineTo( 9.4, 16.2 );
		c.lineTo( 16.6, 16.2 );
		c.closePath();
		c.shadowColor = 'rgba(0,0,0,0.4)';
		c.shadowBlur = 8;
		c.shadowOffsetY = 3;
		c.fillStyle = '#f4f2ec';
		c.fill();
		c.shadowColor = 'transparent';
		c.lineWidth = 1.4;
		c.strokeStyle = '#141412';
		c.stroke();
		c.restore();
	}
	function ripple( x, y, t, color, size = 60 )
	{
		if ( t < 0 || t > 0.6 )
			return;
		const p = E.outCubic( t / 0.6 );
		ring( x, y, 6 + p * size, color || LOOM.text, 2, ( 1 - p ) * 0.8 );
		dot( x, y, 10 * ( 1 - p ), color || LOOM.text, 0.3 * ( 1 - p ) );
	}

	/* ---------- icons (24-unit grid) ---------- */

	function circ( x, y, r ) { c.moveTo( x + r, y ); c.arc( x, y, r, 0, TAU ); }
	function icon( name, x, y, s, color, a )
	{
		c.save();
		c.translate( x, y );
		c.scale( s / 24, s / 24 );
		c.strokeStyle = css( color, a ?? 1 );
		c.fillStyle = css( color, a ?? 1 );
		c.lineWidth = 2;
		c.lineCap = 'round';
		c.lineJoin = 'round';
		c.beginPath();
		let fill = false;
		switch ( name )
		{
			case 'chat': c.roundRect( 3, 4, 18, 13, 4 ); c.moveTo( 8, 17 ); c.lineTo( 6.5, 21 ); c.lineTo( 12, 17 ); break;
			case 'term': c.roundRect( 2.5, 4, 19, 16, 3 ); c.moveTo( 7, 9.5 ); c.lineTo( 10, 12 ); c.lineTo( 7, 14.5 ); c.moveTo( 12, 15 ); c.lineTo( 17, 15 ); break;
			case 'git': circ( 7, 5, 2.2 ); circ( 7, 19, 2.2 ); circ( 17, 8, 2.2 ); c.moveTo( 7, 7.2 ); c.lineTo( 7, 16.8 ); c.moveTo( 17, 10.2 ); c.bezierCurveTo( 17, 14, 8, 12, 7, 16 ); break;
			case 'globe': circ( 12, 12, 9 ); c.moveTo( 3, 12 ); c.lineTo( 21, 12 ); c.moveTo( 12, 3 ); c.bezierCurveTo( 7, 8, 7, 16, 12, 21 ); c.moveTo( 12, 3 ); c.bezierCurveTo( 17, 8, 17, 16, 12, 21 ); break;
			case 'folder': c.moveTo( 3, 7 ); c.lineTo( 3, 18 ); c.quadraticCurveTo( 3, 19, 4, 19 ); c.lineTo( 20, 19 ); c.quadraticCurveTo( 21, 19, 21, 18 ); c.lineTo( 21, 9 ); c.quadraticCurveTo( 21, 8, 20, 8 ); c.lineTo( 12, 8 ); c.lineTo( 10, 5 ); c.lineTo( 4, 5 ); c.quadraticCurveTo( 3, 5, 3, 6 ); c.closePath(); break;
			case 'code': c.moveTo( 8, 7 ); c.lineTo( 3, 12 ); c.lineTo( 8, 17 ); c.moveTo( 16, 7 ); c.lineTo( 21, 12 ); c.lineTo( 16, 17 ); c.moveTo( 13.5, 5 ); c.lineTo( 10.5, 19 ); break;
			case 'bell': c.moveTo( 6, 16 ); c.lineTo( 6, 11 ); c.arc( 12, 11, 6, Math.PI, 0 ); c.lineTo( 18, 16 ); c.lineTo( 19.5, 17.5 ); c.lineTo( 4.5, 17.5 ); c.closePath(); c.moveTo( 10, 20.5 ); c.lineTo( 14, 20.5 ); break;
			case 'grid': c.roundRect( 3.5, 3.5, 7, 7, 1.5 ); c.roundRect( 13.5, 3.5, 7, 7, 1.5 ); c.roundRect( 3.5, 13.5, 7, 7, 1.5 ); c.roundRect( 13.5, 13.5, 7, 7, 1.5 ); break;
			case 'branch': circ( 6, 5, 2.2 ); circ( 6, 19, 2.2 ); circ( 18, 19, 2.2 ); c.moveTo( 6, 7.2 ); c.lineTo( 6, 16.8 ); c.moveTo( 6, 10 ); c.bezierCurveTo( 6, 14, 18, 12, 18, 16.8 ); break;
			case 'doc': c.moveTo( 6, 3 ); c.lineTo( 14, 3 ); c.lineTo( 19, 8 ); c.lineTo( 19, 21 ); c.lineTo( 6, 21 ); c.closePath(); c.moveTo( 14, 3 ); c.lineTo( 14, 8 ); c.lineTo( 19, 8 ); c.moveTo( 9, 13 ); c.lineTo( 16, 13 ); c.moveTo( 9, 17 ); c.lineTo( 14, 17 ); break;
			case 'file': c.moveTo( 6, 3 ); c.lineTo( 14, 3 ); c.lineTo( 19, 8 ); c.lineTo( 19, 21 ); c.lineTo( 6, 21 ); c.closePath(); c.moveTo( 14, 3 ); c.lineTo( 14, 8 ); c.lineTo( 19, 8 ); break;
			case 'cloud': c.moveTo( 7, 18 ); c.arc( 7.5, 14, 4, Math.PI * 0.55, Math.PI * 1.45 ); c.arc( 12, 10, 5, Math.PI * 1.1, Math.PI * 1.95 ); c.arc( 17, 14, 4, Math.PI * 1.5, Math.PI * 0.5 ); c.closePath(); break;
			case 'spark': c.moveTo( 12, 2.5 ); c.quadraticCurveTo( 13, 11, 21.5, 12 ); c.quadraticCurveTo( 13, 13, 12, 21.5 ); c.quadraticCurveTo( 11, 13, 2.5, 12 ); c.quadraticCurveTo( 11, 11, 12, 2.5 ); fill = true; break;
			case 'monitor': c.roundRect( 2.5, 4, 19, 12.5, 2 ); c.moveTo( 12, 16.5 ); c.lineTo( 12, 20 ); c.moveTo( 8, 20.5 ); c.lineTo( 16, 20.5 ); break;
			case 'phone': c.roundRect( 6.5, 2.5, 11, 19, 2.5 ); c.moveTo( 11, 18.5 ); c.lineTo( 13, 18.5 ); break;
			case 'check': circ( 12, 12, 9 ); c.moveTo( 8, 12.5 ); c.lineTo( 11, 15.5 ); c.lineTo( 16.5, 9 ); break;
			case 'tick': c.moveTo( 5, 12.5 ); c.lineTo( 10, 17.5 ); c.lineTo( 19.5, 7 ); break;
			case 'refresh': c.arc( 12, 12, 8, -Math.PI * 0.35, Math.PI * 1.35 ); c.moveTo( 18.6, 3.8 ); c.lineTo( 18.6, 8.2 ); c.lineTo( 14.2, 8.2 ); break;
			case 'pr': circ( 6, 5, 2.2 ); circ( 6, 19, 2.2 ); circ( 18, 19, 2.2 ); c.moveTo( 6, 7.2 ); c.lineTo( 6, 16.8 ); c.moveTo( 18, 16.8 ); c.lineTo( 18, 9 ); c.quadraticCurveTo( 18, 6, 15, 6 ); c.lineTo( 11, 6 ); c.moveTo( 13, 3.8 ); c.lineTo( 10.8, 6 ); c.lineTo( 13, 8.2 ); break;
			case 'nodes': c.roundRect( 3, 3, 7, 6, 1.5 ); c.roundRect( 14, 9, 7, 6, 1.5 ); c.roundRect( 3, 15, 7, 6, 1.5 ); c.moveTo( 10, 6 ); c.lineTo( 14, 11 ); c.moveTo( 10, 18 ); c.lineTo( 14, 13 ); break;
			case 'flame': c.moveTo( 3, 20 ); c.lineTo( 21, 20 ); c.moveTo( 4, 16 ); c.lineTo( 20, 16 ); c.moveTo( 6, 12 ); c.lineTo( 15, 12 ); c.moveTo( 8, 8 ); c.lineTo( 12, 8 ); c.moveTo( 9, 4 ); c.lineTo( 10, 4 ); break;
			case 'music': circ( 7, 18, 2.6 ); circ( 17, 16, 2.6 ); c.moveTo( 9.6, 18 ); c.lineTo( 9.6, 6 ); c.lineTo( 19.6, 4 ); c.lineTo( 19.6, 16 ); c.moveTo( 9.6, 9 ); c.lineTo( 19.6, 7 ); break;
			case 'cube': c.moveTo( 12, 3 ); c.lineTo( 20, 7.5 ); c.lineTo( 20, 16.5 ); c.lineTo( 12, 21 ); c.lineTo( 4, 16.5 ); c.lineTo( 4, 7.5 ); c.closePath(); c.moveTo( 4, 7.5 ); c.lineTo( 12, 12 ); c.lineTo( 20, 7.5 ); c.moveTo( 12, 12 ); c.lineTo( 12, 21 ); break;
			case 'timer': circ( 12, 13.5, 7.5 ); c.moveTo( 12, 13.5 ); c.lineTo( 12, 9.5 ); c.moveTo( 10, 3 ); c.lineTo( 14, 3 ); break;
			case 'kanban': c.roundRect( 3, 3.5, 5, 17, 1.5 ); c.roundRect( 9.5, 3.5, 5, 11, 1.5 ); c.roundRect( 16, 3.5, 5, 7, 1.5 ); break;
			case 'chart': c.moveTo( 3, 20 ); c.lineTo( 21, 20 ); c.moveTo( 4, 15 ); c.lineTo( 9, 10 ); c.lineTo( 13, 13 ); c.lineTo( 20, 5 ); break;
			case 'sun': circ( 12, 12, 4.2 ); for ( let i = 0; i < 8; i++ ) { const an = i * Math.PI / 4; c.moveTo( 12 + Math.cos( an ) * 7, 12 + Math.sin( an ) * 7 ); c.lineTo( 12 + Math.cos( an ) * 9.5, 12 + Math.sin( an ) * 9.5 ); } break;
			case 'pixel': c.rect( 4, 4, 6, 6 ); c.rect( 14, 4, 6, 6 ); c.rect( 4, 14, 6, 6 ); c.rect( 14, 14, 6, 6 ); fill = true; break;
			case 'api': c.moveTo( 7, 5 ); c.quadraticCurveTo( 4, 5, 4, 8 ); c.lineTo( 4, 10 ); c.quadraticCurveTo( 4, 12, 2.5, 12 ); c.quadraticCurveTo( 4, 12, 4, 14 ); c.lineTo( 4, 16 ); c.quadraticCurveTo( 4, 19, 7, 19 ); c.moveTo( 17, 5 ); c.quadraticCurveTo( 20, 5, 20, 8 ); c.lineTo( 20, 10 ); c.quadraticCurveTo( 20, 12, 21.5, 12 ); c.quadraticCurveTo( 20, 12, 20, 14 ); c.lineTo( 20, 16 ); c.quadraticCurveTo( 20, 19, 17, 19 ); break;
			case 'note': c.moveTo( 5, 4 ); c.lineTo( 19, 4 ); c.lineTo( 19, 15 ); c.lineTo( 14, 20 ); c.lineTo( 5, 20 ); c.closePath(); c.moveTo( 14, 20 ); c.lineTo( 14, 15 ); c.lineTo( 19, 15 ); break;
			case 'plus': c.moveTo( 12, 5 ); c.lineTo( 12, 19 ); c.moveTo( 5, 12 ); c.lineTo( 19, 12 ); break;
			case 'lock': c.roundRect( 5, 10.5, 14, 10, 2 ); c.moveTo( 8, 10.5 ); c.lineTo( 8, 8 ); c.arc( 12, 8, 4, Math.PI, 0 ); c.lineTo( 16, 10.5 ); break;
			case 'search': circ( 10.5, 10.5, 6 ); c.moveTo( 15, 15 ); c.lineTo( 20, 20 ); break;
			case 'gear': circ( 12, 12, 3.2 ); for ( let i = 0; i < 6; i++ ) { const an = i * Math.PI / 3; c.moveTo( 12 + Math.cos( an ) * 6, 12 + Math.sin( an ) * 6 ); c.lineTo( 12 + Math.cos( an ) * 9, 12 + Math.sin( an ) * 9 ); } circ( 12, 12, 6.5 ); break;
			case 'play': c.moveTo( 7, 4.5 ); c.lineTo( 19, 12 ); c.lineTo( 7, 19.5 ); c.closePath(); fill = true; break;
			case 'arrowUp': c.moveTo( 12, 18 ); c.lineTo( 12, 6 ); c.moveTo( 7, 11 ); c.lineTo( 12, 6 ); c.lineTo( 17, 11 ); break;
			case 'chev': c.moveTo( 9, 6 ); c.lineTo( 15, 12 ); c.lineTo( 9, 18 ); break;
			case 'chevDown': c.moveTo( 6, 9 ); c.lineTo( 12, 15 ); c.lineTo( 18, 9 ); break;
			case 'puzzle': c.moveTo( 4, 8 ); c.lineTo( 9, 8 ); c.arc( 11.5, 7, 2.5, Math.PI * 0.9, Math.PI * 0.1 ); c.lineTo( 19, 8 ); c.lineTo( 19, 12 ); c.arc( 18, 14.5, 2.5, -Math.PI * 0.4, Math.PI * 0.4 ); c.lineTo( 19, 20 ); c.lineTo( 4, 20 ); c.closePath(); break;
			default: c.rect( 4, 4, 16, 16 );
		}
		if ( fill )
			c.fill();
		else
			c.stroke();
		c.restore();
	}

	/** Thin animated thread used as the "working" indicator. */
	function stitch( x, y, w, t, color, amp = 3 )
	{
		c.save();
		c.beginPath();
		for ( let i = 0; i <= 24; i++ )
		{
			const u = i / 24, px = x + u * w, py = y + Math.sin( u * TAU * 1.5 - t * 7 ) * amp;
			if ( i === 0 )
				c.moveTo( px, py );
			else
				c.lineTo( px, py );
		}
		c.setLineDash( [ 5, 4 ] );
		c.lineDashOffset = -t * 30;
		c.strokeStyle = css( color );
		c.lineWidth = 2;
		c.lineCap = 'round';
		c.stroke();
		c.restore();
	}
	function spinner( x, y, r, t, color, lw = 2 )
	{
		c.save();
		c.beginPath();
		const a = t * 7;
		c.arc( x, y, r, a, a + Math.PI * 1.3 );
		c.strokeStyle = css( color );
		c.lineWidth = lw;
		c.lineCap = 'round';
		c.stroke();
		c.restore();
	}

	/** Settings-style switch; on runs 0 (off) … 1 (on). */
	function toggle( x, y, on, P, w = 40, h = 22 )
	{
		const k = clamp( on );
		fillR( x, y, w, h, h / 2, mix( P.hover, P.accent, k ) );
		dot( lerp( x + h / 2, x + w - h / 2, E.inOutCubic( k ) ), y + h / 2, h / 2 - 4, mix( P.muted, P.canvas, k ) );
	}

	/* ---------- Loom UI ---------- */

	/** Panel frame with a header; returns the content rectangle. */
	function panel( x, y, w, h, P, o )
	{
		o = o || {};
		const r = o.r ?? 10;
		if ( o.shadow !== false )
			shadow( o.shadowBlur ?? 30, o.shadowY ?? 10, o.shadowA ?? 0.35, () => fillR( x, y, w, h, r, P.panel ) );
		else
			fillR( x, y, w, h, r, P.panel );
		strokeR( x + 0.5, y + 0.5, w - 1, h - 1, r, o.active ? P.accent : P.border, 1, o.active ? 0.55 : 1 );
		if ( o.title == null )
			return { x, y, w, h };
		c.save();
		clipR( x, y, w, 34, 0 );
		icon( o.icon || 'chat', x + 12, y + 9, 16, P.muted );
		txt( o.title, x + 36, y + 22, 13, P.text, { w: 500 } );
		if ( o.right )
			txt( o.right, x + w - 14, y + 22, 12, P.muted, { f: FM, align: 'right' } );
		c.restore();
		line( x + 1, y + 34, x + w - 1, y + 34, P.divider, 1 );
		return { x, y: y + 35, w, h: h - 35 };
	}

	/** Terminal content: lines are { at, s, color, bold, cps } (cps types the line). */
	function terminal( r, P, t, lines, fs = 13 )
	{
		c.save();
		clipR( r.x, r.y, r.w, r.h, 0 );
		c.fillStyle = css( P.term );
		c.fillRect( r.x, r.y, r.w, r.h );
		const lh = fs * 1.6;
		let y = r.y + 14 + fs, lastEnd = 0;
		for ( const ln of lines )
		{
			if ( t < ln.at )
				break;
			let s = ln.s;
			if ( ln.cps )
				s = s.slice( 0, Math.floor( ( t - ln.at ) * ln.cps ) );
			if ( ln.prompt )
			{
				txt( 'PS', r.x + 14, y, fs, P.accent, { f: FM, w: 600 } );
				txt( ln.prompt, r.x + 14 + fs * 1.6, y, fs, P.blue, { f: FM } );
				const pw = measure( 'PS ' + ln.prompt + ' ', fs, 400, FM );
				txt( s, r.x + 14 + pw, y, fs, P.text, { f: FM } );
				lastEnd = r.x + 14 + pw + measure( s, fs, 400, FM );
			}
			else
			{
				txt( s, r.x + 14, y, fs, ln.color ? P[ ln.color ] : P.text, { f: FM, w: ln.bold ? 700 : 400 } );
				lastEnd = r.x + 14 + measure( s, fs, ln.bold ? 700 : 400, FM );
			}
			y += lh;
		}
		if ( frac( t * 1.6 ) < 0.6 )
			fillR( lastEnd + 2, y - lh - fs * 0.85, fs * 0.55, fs * 1.1, 1, P.text, 0.8 );
		c.restore();
	}

	const GIT_ROWS = [
		[ 0, 'Pricing: lazy-load plan images', 'a41f9c2', 'perf/pricing' ],
		[ 1, 'Hero: new headline', '9be03d1', 'hero' ],
		[ 0, 'Split the pricing bundle', '77c2e10' ],
		[ 2, 'Footer: fix broken links', 'c1d84f5', 'footer' ],
		[ 1, 'Hero: tighten the copy', '5e3a9b7' ],
		[ 0, 'Merge branch hero', 'f02b6aa' ],
		[ 0, 'Preload the pricing font', '3d91c08' ],
		[ 2, 'Footer: add a sitemap', '8a0e7d1' ],
		[ 0, 'Release 2.4', '1c7b3e5', 'v2.4' ],
		[ 0, 'Docs: pricing FAQ', 'e4d2a90' ],
	];
	function gitGraph( r, P, t, start )
	{
		c.save();
		clipR( r.x, r.y, r.w, r.h, 0 );
		const lanes = [ P.accent, P.blue, P.success ];
		const rowH = 30, x0 = r.x + 22, y0 = r.y + 22;
		const lx = l => x0 + l * 16;
		const ry = i => y0 + i * rowH;
		c.lineWidth = 2;
		c.lineCap = 'round';
		const seg_ = ( x1, y1, x2, y2, col ) =>
		{
			c.beginPath();
			c.moveTo( x1, y1 );
			if ( x1 === x2 )
				c.lineTo( x2, y2 );
			else
				c.bezierCurveTo( x1, y1 + ( y2 - y1 ) * 0.6, x2, y1 + ( y2 - y1 ) * 0.4, x2, y2 );
			c.strokeStyle = css( col );
			c.stroke();
		};
		const grow = E.outCubic( seg( t, start, start + 0.7 ) );
		c.save();
		c.beginPath();
		c.rect( r.x, r.y, r.w, ( r.h + 20 ) * grow );
		c.clip();
		seg_( lx( 0 ), ry( 0 ), lx( 0 ), ry( 10 ), lanes[ 0 ] );
		seg_( lx( 1 ), ry( 1 ), lx( 1 ), ry( 4 ), lanes[ 1 ] );
		seg_( lx( 1 ), ry( 4 ), lx( 0 ), ry( 5 ), lanes[ 1 ] );
		seg_( lx( 2 ), ry( 3 ), lx( 2 ), ry( 7 ), lanes[ 2 ] );
		seg_( lx( 2 ), ry( 7 ), lx( 0 ), ry( 8 ), lanes[ 2 ] );
		c.restore();
		GIT_ROWS.forEach( ( row, i ) =>
		{
			const a = seg( t, start + i * 0.05, start + i * 0.05 + 0.3 );
			if ( a <= 0 )
				return;
			alpha( a, () =>
			{
				const y = ry( i );
				dot( lx( row[ 0 ] ), y, 5, lanes[ row[ 0 ] ] );
				dot( lx( row[ 0 ] ), y, 2.2, P.panel );
				let tx = x0 + 3 * 16 + 8;
				if ( row[ 3 ] )
				{
					const bw = measure( row[ 3 ], 11, 600 ) + 14;
					fillR( tx, y - 9, bw, 18, 5, lanes[ row[ 0 ] ], 0.18 );
					txt( row[ 3 ], tx + 7, y + 4, 11, lanes[ row[ 0 ] ], { w: 600 } );
					tx += bw + 8;
				}
				txt( row[ 1 ], tx, y + 4.5, 13, P.text );
				txt( row[ 2 ], r.x + r.w - 14, y + 4.5, 12, P.muted, { f: FM, align: 'right' } );
			} );
		} );
		c.restore();
	}

	root.LK = {
		W, H, TAU, FD, FT, FM, clamp, seg, lerp, frac, E, spring, rnd,
		css, mix, mixPal, LOOM, THEMES, use, ctx, setFont, measure, txt, wrap, rrect, fillR, strokeR, line, dot, ring,
		clipR, alpha, scaled, glow, shadow, bez, Poly, logo, pointer,
		ripple, icon, stitch, spinner, toggle, panel, terminal, gitGraph, SITE,
	};
} )( typeof window !== 'undefined' ? window : globalThis );
