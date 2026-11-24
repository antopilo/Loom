// Stage for the Loom trailer: paper and grain under the film, offscreen layers for the UI, a flat camera that
// pans and zooms over a layer, and rough hand-drawn lines.
( function ( root )
{
	'use strict';

	const L = root.LK;
	const { W, H, clamp, lerp } = L;

	/* ---------- layers ---------- */

	const layers = new Map();
	/** A cleared offscreen canvas of w x h units drawn at scale; its context is already scaled. */
	function layer( name, w, h, scale = 1 )
	{
		let l = layers.get( name );
		const cw = Math.round( w * scale ), ch = Math.round( h * scale );
		if ( !l || l.canvas.width !== cw || l.canvas.height !== ch )
		{
			const canvas = document.createElement( 'canvas' );
			canvas.width = cw;
			canvas.height = ch;
			l = { canvas, ctx: canvas.getContext( '2d' ), w, h, scale };
			layers.set( name, l );
		}
		l.ctx.setTransform( 1, 0, 0, 1, 0, 0 );
		l.ctx.globalAlpha = 1;
		l.ctx.filter = 'none';
		l.ctx.clearRect( 0, 0, cw, ch );
		l.ctx.setTransform( scale, 0, 0, scale, 0, 0 );
		return l;
	}

	/* ---------- camera: pans and zooms over a flat layer ---------- */

	/** Screen point of layer point (u, v) for camera cam = { fx, fy, sx, sy, zoom }: the focus (fx, fy) lands on (sx, sy). */
	function project( cam, u, v )
	{
		return [ cam.sx + ( u - cam.fx ) * cam.zoom, cam.sy + ( v - cam.fy ) * cam.zoom ];
	}

	/** Keyframed camera: keys are [ t, { fx, fy, sx, sy, zoom } ]; eases between neighbours. */
	function camAt( keys, t )
	{
		if ( t <= keys[ 0 ][ 0 ] )
			return keys[ 0 ][ 1 ];
		for ( let i = 0; i < keys.length - 1; i++ )
		{
			const a = keys[ i ], b = keys[ i + 1 ];
			if ( t < b[ 0 ] )
			{
				const p = L.E.inOutCubic( L.seg( t, a[ 0 ], b[ 0 ] ) );
				const o = {};
				for ( const k in a[ 1 ] )
					o[ k ] = lerp( a[ 1 ][ k ], b[ 1 ][ k ] ?? a[ 1 ][ k ], p );
				return o;
			}
		}
		return keys[ keys.length - 1 ][ 1 ];
	}

	/** Draws layer l onto ctx through camera cam. o: { alpha, shadow (0…1, a soft drop shadow from the layer's own alpha) }. */
	function screen( ctx, l, cam, o )
	{
		o = o || {};
		const a = o.alpha ?? 1;
		if ( a <= 0.002 )
			return;
		const [ x, y ] = project( cam, 0, 0 );
		const sh = o.shadow ?? 1;
		ctx.save();
		ctx.setTransform( 1, 0, 0, 1, 0, 0 );
		ctx.globalAlpha = a;
		ctx.filter = 'none';
		ctx.imageSmoothingEnabled = true;
		ctx.imageSmoothingQuality = 'high';
		if ( sh > 0 )
		{
			ctx.shadowColor = `rgba(38,48,43,${ ( 0.38 * sh ).toFixed( 3 ) })`;
			ctx.shadowBlur = 70;
			ctx.shadowOffsetY = 28;
		}
		ctx.drawImage( l.canvas, x, y, l.w * cam.zoom, l.h * cam.zoom );
		ctx.restore();
	}

	/* ---------- paper and grain ---------- */

	let paperCanvas = null, paperColor = null;
	const grainTiles = [];
	/** Draws the paper ground: base colour, soft mottling, fibres and fine tooth. Built once. */
	function paper( ctx, color )
	{
		if ( !paperCanvas || paperColor !== color )
		{
			paperColor = color;
			paperCanvas = document.createElement( 'canvas' );
			paperCanvas.width = W;
			paperCanvas.height = H;
			const g = paperCanvas.getContext( '2d' );
			g.fillStyle = color;
			g.fillRect( 0, 0, W, H );
			// Mottling: a few octaves of blurred value noise.
			for ( const [ cell, amt ] of [ [ 240, 0.05 ], [ 90, 0.04 ], [ 30, 0.03 ] ] )
			{
				const nw = Math.ceil( W / cell ) + 2, nh = Math.ceil( H / cell ) + 2;
				const small = document.createElement( 'canvas' );
				small.width = nw;
				small.height = nh;
				const s = small.getContext( '2d' ), img = s.createImageData( nw, nh );
				for ( let i = 0; i < nw * nh; i++ )
				{
					const v = L.rnd( i, cell ) > 0.5;
					img.data[ i * 4 ] = img.data[ i * 4 + 1 ] = img.data[ i * 4 + 2 ] = v ? 255 : 0;
					img.data[ i * 4 + 3 ] = Math.round( 255 * Math.abs( L.rnd( i, cell + 1 ) - 0.5 ) * 2 * amt );
				}
				s.putImageData( img, 0, 0 );
				g.imageSmoothingEnabled = true;
				g.imageSmoothingQuality = 'high';
				g.filter = `blur(${ cell * 0.35 }px)`;
				g.drawImage( small, -cell, -cell, nw * cell, nh * cell );
				g.filter = 'none';
			}
			// Fibres: short curved strokes, lighter and darker than the stock.
			g.lineCap = 'round';
			for ( let i = 0; i < 2600; i++ )
			{
				const x = L.rnd( i, 11 ) * W, y = L.rnd( i, 12 ) * H, len = 6 + L.rnd( i, 13 ) * 26, an = L.rnd( i, 14 ) * Math.PI * 2;
				const bend = ( L.rnd( i, 15 ) - 0.5 ) * 10;
				g.strokeStyle = L.rnd( i, 16 ) > 0.55 ? 'rgba(255,255,250,0.10)' : 'rgba(40,52,46,0.07)';
				g.lineWidth = 0.6 + L.rnd( i, 17 ) * 0.8;
				g.beginPath();
				g.moveTo( x, y );
				g.quadraticCurveTo( x + Math.cos( an ) * len / 2 - Math.sin( an ) * bend, y + Math.sin( an ) * len / 2 + Math.cos( an ) * bend, x + Math.cos( an ) * len, y + Math.sin( an ) * len );
				g.stroke();
			}
			// Tooth: per-pixel grain.
			const img = g.getImageData( 0, 0, W, H ), d = img.data;
			for ( let i = 0, k = 0; i < d.length; i += 4, k++ )
			{
				const n = ( L.rnd( k, 21 ) - 0.5 ) * 14;
				d[ i ] += n;
				d[ i + 1 ] += n;
				d[ i + 2 ] += n;
			}
			g.putImageData( img, 0, 0 );
		}
		ctx.drawImage( paperCanvas, 0, 0 );
	}

	/** Light moving film grain over the whole frame. */
	function grain( ctx, t, amount = 0.05 )
	{
		if ( !grainTiles.length )
			for ( let k = 0; k < 4; k++ )
			{
				const cv = document.createElement( 'canvas' );
				cv.width = cv.height = 256;
				const g = cv.getContext( '2d' ), img = g.createImageData( 256, 256 );
				for ( let i = 0; i < 256 * 256; i++ )
				{
					const v = Math.round( L.rnd( i, 40 + k ) * 255 );
					img.data[ i * 4 ] = img.data[ i * 4 + 1 ] = img.data[ i * 4 + 2 ] = v;
					img.data[ i * 4 + 3 ] = 255;
				}
				g.putImageData( img, 0, 0 );
				grainTiles.push( cv );
			}
		const f = Math.floor( t * 24 );
		ctx.save();
		ctx.setTransform( 1, 0, 0, 1, 0, 0 );
		ctx.globalAlpha = amount;
		ctx.globalCompositeOperation = 'overlay';
		ctx.fillStyle = ctx.createPattern( grainTiles[ f % 4 ], 'repeat' );
		ctx.translate( Math.floor( L.rnd( f, 1 ) * 256 ), Math.floor( L.rnd( f, 2 ) * 256 ) );
		ctx.fillRect( -256, -256, W + 512, H + 512 );
		ctx.restore();
	}

	/* ---------- rough lines ---------- */

	/** Smooth 1-D value noise in -1…1. */
	function noise1( x, seed )
	{
		const i = Math.floor( x ), f = x - i, u = f * f * ( 3 - 2 * f );
		return lerp( L.rnd( i, seed ) * 2 - 1, L.rnd( i + 1, seed ) * 2 - 1, u );
	}

	/**
	 * A hand-drawn stroke along the points pts (already smooth), revealed up to fraction p:
	 * the path wobbles, the width breathes like pen pressure, the ends taper, and a faint second pass doubles it.
	 */
	function rough( ctx, pts, p, color, lw, a = 1, seed = 1 )
	{
		if ( p <= 0 || a <= 0 || pts.length < 2 )
			return;
		const lens = [ 0 ];
		for ( let i = 1; i < pts.length; i++ )
			lens.push( lens[ i - 1 ] + Math.hypot( pts[ i ][ 0 ] - pts[ i - 1 ][ 0 ], pts[ i ][ 1 ] - pts[ i - 1 ][ 1 ] ) );
		const total = lens[ lens.length - 1 ], end = total * clamp( p );
		ctx.save();
		ctx.lineCap = 'round';
		ctx.strokeStyle = color;
		for ( const pass of [ 0, 1 ] )
		{
			const off = pass ? 1.1 : 0, pa = pass ? 0.32 : 0.9;
			for ( let i = 1; i < pts.length && lens[ i - 1 ] < end; i++ )
			{
				const s0 = lens[ i - 1 ], s1 = Math.min( lens[ i ], end );
				const at = ( s, q ) =>
				{
					const k = ( s - lens[ i - 1 ] ) / Math.max( 1e-6, lens[ i ] - lens[ i - 1 ] );
					const x = lerp( pts[ i - 1 ][ 0 ], pts[ i ][ 0 ], k ), y = lerp( pts[ i - 1 ][ 1 ], pts[ i ][ 1 ], k );
					const dx = pts[ i ][ 0 ] - pts[ i - 1 ][ 0 ], dy = pts[ i ][ 1 ] - pts[ i - 1 ][ 1 ], dl = Math.hypot( dx, dy ) || 1;
					const j = noise1( s / 38, seed + pass * 7 ) * 1.4 + noise1( s / 9, seed + 3 + pass ) * 0.45 + off * q;
					return [ x - dy / dl * j, y + dx / dl * j ];
				};
				const p0 = at( s0, 1 ), p1 = at( s1, 1 );
				const mid = ( s0 + s1 ) / 2;
				const taper = Math.min( 1, mid / 18, ( total - mid ) / 26 + 0.25 );
				const pressure = 0.75 + 0.35 * noise1( mid / 60, seed + 11 );
				ctx.globalAlpha = a * pa * ( 0.85 + 0.15 * noise1( mid / 14, seed + 5 ) );
				ctx.lineWidth = Math.max( 0.4, lw * pressure * taper * ( pass ? 0.55 : 1 ) );
				ctx.beginPath();
				ctx.moveTo( p0[ 0 ], p0[ 1 ] );
				ctx.lineTo( p1[ 0 ], p1[ 1 ] );
				ctx.stroke();
			}
		}
		ctx.restore();
	}

	root.LoomStage = { layer, project, camAt, screen, paper, grain, rough };
} )( typeof window !== 'undefined' ? window : globalThis );
