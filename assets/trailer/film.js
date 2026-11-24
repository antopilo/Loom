// The Loom trailer, staged in a calm product-film style: paper ground, serif lines whose words fill in one by one,
// Loom's real window as a glass screen in perspective, and rough hand-drawn marks.
// LoomTrailer.frame( ctx, t ) draws the frame at t seconds; scene times come from timeline.js.
( function ( root )
{
	'use strict';

	const L = root.LK, PT = root.LoomParts, TL = root.LoomTimeline, S = root.LoomStage, UI = root.LoomUI;
	const { W, H, clamp, seg, lerp, E, spring } = L;
	const P = L.LOOM;
	const T = TL.TURN;
	let c = null;

	const BG = '#cad5cc';
	const INK = '#1d221f';
	const PENDING = '#8e9a92';
	const PEN = '#59625d';
	const SERIF = '"Newsreader", Georgia, serif';
	/** The Loom site's easing, cubic-bezier( 0.2, 0.7, 0.1, 1 ), for every entrance. */
	const ease = L.SITE;

	/** Points every drawing module at ctx. */
	function useCtx( ctx )
	{
		PT.use( ctx );
		UI.use( ctx );
	}

	/** Runs fn with every drawing module pointed at layer l, then back at the frame. */
	function inLayer( l, fn )
	{
		const frameCtx = c;
		useCtx( l.ctx );
		c = l.ctx;
		fn();
		c = frameCtx;
		useCtx( frameCtx );
	}

	/* ======================================================================
	   Type and drawing helpers
	   ====================================================================== */

	/** Serif lines whose words appear one by one: each fades in light, then fills to ink. */
	function say( lines, cx, cy, size, t, o )
	{
		o = o || {};
		if ( t < 0 )
			return;
		const out = o.out != null && o.out > 0 ? 1 - E.inCubic( seg( o.out, 0, 0.35 ) ) : 1;
		if ( out <= 0 )
			return;
		const lh = size * 1.06, ls = -size * 0.02;
		L.setFont( size, o.w || 500, SERIF, ls );
		const space = c.measureText( ' ' ).width;
		const rows = lines.map( ln => ln.split( ' ' ).map( s => ( { s, w: c.measureText( s ).width } ) ) );
		let k = 0;
		const y0 = cy - ( rows.length - 1 ) * lh / 2 + size * 0.34;
		rows.forEach( ( row, ri ) =>
		{
			const width = row.reduce( ( a, b ) => a + b.w, 0 ) + space * ( row.length - 1 );
			let x = o.align === 'left' ? cx : cx - width / 2;
			const y = y0 + ri * lh;
			for ( const item of row )
			{
				const st = k++ * ( o.stagger ?? 0.07 );
				const a = ease( seg( t, st, st + 0.3 ) ) * out;
				if ( a > 0.002 )
				{
					L.setFont( size, o.w || 500, SERIF, ls );
					c.fillStyle = L.css( L.mix( PENDING, o.color || INK, ease( seg( t, st + 0.1, st + 0.55 ) ) ), a );
					c.textAlign = 'left';
					c.textBaseline = 'alphabetic';
					c.fillText( item.s, x, y + ( 1 - ease( seg( t, st, st + 0.45 ) ) ) * size * 0.08 - ( 1 - out ) * size * 0.12 );
				}
				x += item.w + space;
			}
		} );
		c.letterSpacing = '0px';
	}

	/** Catmull-Rom points through pts, cached by key. */
	const curves = new Map();
	function curve( key, pts )
	{
		const hit = curves.get( key );
		if ( hit && hit.src === JSON.stringify( pts ) )
			return hit.pts;
		const out = [];
		for ( let i = 0; i < pts.length - 1; i++ )
		{
			const p0 = pts[ Math.max( 0, i - 1 ) ], p1 = pts[ i ], p2 = pts[ i + 1 ], p3 = pts[ Math.min( pts.length - 1, i + 2 ) ];
			for ( let s = 0; s < 16; s++ )
			{
				const u = s / 16, u2 = u * u, u3 = u2 * u;
				out.push( [ 0, 1 ].map( d => 0.5 * ( 2 * p1[ d ] + ( -p0[ d ] + p2[ d ] ) * u + ( 2 * p0[ d ] - 5 * p1[ d ] + 4 * p2[ d ] - p3[ d ] ) * u2 + ( -p0[ d ] + 3 * p1[ d ] - 3 * p2[ d ] + p3[ d ] ) * u3 ) ) );
			}
		}
		out.push( pts[ pts.length - 1 ] );
		curves.set( key, { src: JSON.stringify( pts ), pts: out } );
		return out;
	}

	/** A rough hand-drawn line through pts, revealed by p. */
	function pen( key, pts, p, a = 1, lw = 2.6, seed = 1 )
	{
		S.rough( c, curve( key, pts ), ease( clamp( p ) ), PEN, lw, a, seed );
	}

	/** A rough hand-drawn arrow from a to b that bows by bend; the head draws once the shaft is done. */
	function arrow( key, a, b, p, alpha, bend = 0.18, seed = 3 )
	{
		if ( p <= 0 || alpha <= 0 )
			return;
		const mx = ( a[ 0 ] + b[ 0 ] ) / 2, my = ( a[ 1 ] + b[ 1 ] ) / 2;
		const nx = -( b[ 1 ] - a[ 1 ] ) * bend, ny = ( b[ 0 ] - a[ 0 ] ) * bend;
		const pts = curve( key, [ a, [ mx + nx, my + ny ], b ].map( q => q.map( v => Math.round( v ) ) ) );
		S.rough( c, pts, ease( seg( p, 0, 0.75 ) ), PEN, 2.6, alpha, seed );
		const hp = ease( seg( p, 0.7, 1 ) );
		if ( hp > 0 )
		{
			const e = pts[ pts.length - 1 ], q = pts[ pts.length - 5 ];
			const an = Math.atan2( e[ 1 ] - q[ 1 ], e[ 0 ] - q[ 0 ] );
			for ( const side of [ -1, 1 ] )
			{
				const h = [ e, [ e[ 0 ] - Math.cos( an + side * 0.5 ) * 20, e[ 1 ] - Math.sin( an + side * 0.5 ) * 20 ] ];
				S.rough( c, [ h[ 0 ], [ lerp( h[ 0 ][ 0 ], h[ 1 ][ 0 ], 0.5 ), lerp( h[ 0 ][ 1 ], h[ 1 ][ 1 ], 0.5 ) ], h[ 1 ] ], hp, PEN, 2.4, alpha, seed + side + 5 );
			}
		}
	}

	/** Loom's app tile: the mark on a rounded light square. */
	function tile( cx, cy, size, a = 1 )
	{
		L.alpha( a, () =>
		{
			L.shadow( size * 0.3, size * 0.08, 0.35, () => L.fillR( cx - size / 2, cy - size / 2, size, size, size * 0.24, '#fbfaf6' ) );
			L.logo( cx, cy, size * 0.66, { ring: INK } );
		} );
	}

	/** A small outlined label, like the platform tag above a product headline. */
	function pill( text, cx, cy, a )
	{
		if ( a <= 0 )
			return;
		L.alpha( a, () =>
		{
			const w = L.measure( text, 20, 500 ) + 36;
			L.fillR( cx - w / 2, cy - 20, w, 40, 10, '#ffffff', 0.35 );
			L.strokeR( cx - w / 2 + 0.5, cy - 19.5, w - 1, 39, 10, '#6d7771', 1, 0.6 );
			L.txt( text, cx, cy + 7, 20, '#3d4540', { w: 500, align: 'center' } );
		} );
	}

	/** Key caps for a shortcut, centred at (cx, cy); t is seconds since they appeared. */
	function keys( list, cx, cy, t, hold )
	{
		if ( t < 0 || t > hold + 0.3 )
			return;
		const ws = list.map( k => Math.max( 58, L.measure( k, 22, 500 ) + 34 ) );
		const total = ws.reduce( ( s, w ) => s + w, 0 ) + 12 * ( list.length - 1 );
		let x = cx - total / 2;
		const out = 1 - seg( t, hold, hold + 0.3 );
		list.forEach( ( k, i ) =>
		{
			const p = seg( t, i * 0.06, i * 0.06 + 0.35 ), w = ws[ i ];
			const press = t > 0.1 + i * 0.1 && t < 0.3 + i * 0.1 ? 3 : 0;
			L.alpha( clamp( p * 2 ) * out, () => L.scaled( E.outBack( p ), x + w / 2, cy, () =>
			{
				L.fillR( x, cy - 26 + 5, w, 52, 10, '#9aa59e' );
				L.fillR( x, cy - 26 + press, w, 52, 10, '#f7f6f1' );
				L.strokeR( x + 0.5, cy - 25.5 + press, w - 1, 51, 10, '#b9c1bb', 1 );
				L.txt( k, x + w / 2, cy + 8 + press, 22, INK, { w: 500, align: 'center' } );
			} ) );
			x += w + 12;
		} );
	}

	/** The pointer inside a layer, with a ripple when it clicks at t0. */
	function pointer( x, y, a, t0, t )
	{
		L.ripple( x, y, t - t0, P.accent, 30 );
		L.pointer( x, y, 1.15, a );
	}

	/** Moves the pointer along keys [ t, x, y ], clicks at click and hides at hide. */
	function guide( t, path, click, hide )
	{
		const show = path[ 0 ][ 0 ];
		if ( t < show || t >= hide + 0.25 )
			return;
		const p = PT.pointAt( path, t );
		pointer( p[ 0 ], p[ 1 ], seg( t, show, show + 0.15 ) * ( 1 - seg( t, hide, hide + 0.25 ) ), click, t );
	}

	const lerpR = ( a, b, p ) => a.map( ( v, i ) => lerp( v, b[ i ], p ) );

	/* ======================================================================
	   1 – 3. One continuous shot of the window: every agent, the agent at work, the layout (0 – 24)
	   ====================================================================== */

	/** Camera over the window (window units): the sidebar, the prompt, the turn, the terminal, the browser, then the whole layout. */
	const CAM_WIN = [
		[ 0, { fx: 130, fy: 300, sx: 560, sy: 560, zoom: 2.0 } ],
		[ 4.4, { fx: 135, fy: 300, sx: 560, sy: 555, zoom: 2.1 } ],
		[ 5.9, { fx: 842, fy: 700, sx: 690, sy: 640, zoom: 1.65 } ],
		[ 7.05, { fx: 842, fy: 690, sx: 690, sy: 640, zoom: 1.7 } ],
		[ 7.7, { fx: 842, fy: 230, sx: 700, sy: 430, zoom: 1.6 } ],
		[ 8.5, { fx: 830, fy: 230, sx: 700, sy: 430, zoom: 1.6 } ],
		[ 9.2, { fx: 1170, fy: 200, sx: 760, sy: 450, zoom: 1.7 } ],
		[ 10.4, { fx: 1170, fy: 210, sx: 760, sy: 450, zoom: 1.72 } ],
		[ 11.0, { fx: 1170, fy: 590, sx: 760, sy: 560, zoom: 1.65 } ],
		[ 12.5, { fx: 1170, fy: 590, sx: 760, sy: 560, zoom: 1.68 } ],
		[ 13.1, { fx: 1170, fy: 610, sx: 760, sy: 570, zoom: 1.9 } ],
		[ 15.4, { fx: 1170, fy: 610, sx: 760, sy: 570, zoom: 1.95 } ],
		[ 16.4, { fx: 720, fy: 405, sx: 760, sy: 545, zoom: 0.92 } ],
		[ 24.2, { fx: 720, fy: 405, sx: 760, sy: 545, zoom: 0.96 } ],
	];

	/** Panel layout keys: [ t, rect, alpha?, from? ]; each change springs from the previous rect. */
	const LAYOUT = {
		chat: [ [ 0, [ 0, 0, 1, 1 ] ], [ 8.6, [ 0, 0, 0.56, 1 ] ], [ 17.8, [ 0, 0, 0.56, 0.56 ] ], [ 20.1, [ 0.56, 0.44, 0.44, 0.56 ] ], [ 21.25, [ -0.36, 0, 0.36, 1 ], 0 ] ],
		term: [ [ 0, [ 1, 0, 0, 1 ], 0 ], [ 8.6, [ 0.56, 0, 0.44, 1 ] ], [ 10.6, [ 0.56, 0, 0.44, 0.44 ] ], [ 21.25, [ 0.36, 0.62, 0.64, 0.38 ] ] ],
		browser: [ [ 0, [ 0.56, 1, 0.44, 0 ], 0 ], [ 10.6, [ 0.56, 0.44, 0.44, 0.56 ] ], [ 20.1, [ 0, 0, 0.56, 0.56 ], 1, [ 0, 0, 0.56, 0.56 ] ], [ 21.25, [ 0.36, 0, 0.64, 0.62 ] ] ],
		git: [ [ 0, [ 0, 1, 0.56, 0 ], 0 ], [ 17.8, [ 0, 0.56, 0.56, 0.44 ] ], [ 21.25, [ 0, 0, 0.36, 1 ] ] ],
	};
	const DRAG = { from: [ 0.56, 0.44, 0.44, 0.56 ], to: [ 0, 0, 0.56, 0.56 ], t0: 19.2, t1: 20.1 };

	function keyAt( list, t )
	{
		let i = 0;
		while ( i + 1 < list.length && t >= list[ i + 1 ][ 0 ] )
			i++;
		const cur = list[ i ];
		if ( i === 0 )
			return { r: cur[ 1 ], a: cur[ 2 ] ?? 1 };
		const prev = list[ i - 1 ];
		const p = spring( t - cur[ 0 ], 1.1, 8 );
		return { r: lerpR( cur[ 3 ] || prev[ 1 ], cur[ 1 ], p ), a: lerp( prev[ 2 ] ?? 1, cur[ 2 ] ?? 1, E.outCubic( seg( t, cur[ 0 ], cur[ 0 ] + 0.35 ) ) ) };
	}

	function panelsAt( t )
	{
		const out = Object.keys( LAYOUT ).map( id => Object.assign( { id }, keyAt( LAYOUT[ id ], t ) ) );
		const b = out.find( p => p.id === 'browser' );
		if ( t >= DRAG.t0 && t < DRAG.t1 )
		{
			const lift = E.outCubic( seg( t, DRAG.t0, DRAG.t0 + 0.15 ) );
			b.r = lerpR( DRAG.from, DRAG.to, E.inOutCubic( seg( t, DRAG.t0 + 0.15, DRAG.t1 - 0.05 ) ) );
			b.lift = lift;
			b.scale = 1 - 0.03 * lift;
			b.a = 0.95;
		}
		else if ( t >= DRAG.t1 && t < DRAG.t1 + 0.4 )
			b.lift = 1 - seg( t, DRAG.t1, DRAG.t1 + 0.4 );
		const active = t < 8.6 ? 'chat' : t < 10.6 ? 'term' : t < 17.8 ? 'browser' : t < DRAG.t0 ? 'git' : 'browser';
		out.find( p => p.id === active ).active = true;
		return out;
	}

	const PROMPT = 'Make the pricing page faster, then show me before and after.';
	const TOOLS = [
		[ T.worktree, 'worktrees_enter', 'perf/pricing' ],
		[ T.terminal, 'shell_open_panel', 'terminal' ],
		[ T.browser, 'browser_show', 'localhost:4321/pricing' ],
		[ T.report, 'browser_show', 'before-after.html' ],
	];
	const REPLY = [ { s: 'Pricing now loads in ' }, { s: '0.9 s', b: 1 }, { s: ' instead of ' }, { s: '2.7 s', b: 1 }, { s: '. The report is open on the right.' } ];
	const SWITCH = 4.55;

	function chatState( t )
	{
		if ( t < SWITCH )
			return {
				title: 'Hero copy pass', busy: true, branch: 'hero', context: '34%',
				working: { label: 'Working…', secs: 41 + t },
				items: [
					{ kind: 'user', text: 'Make the hero headline shorter and check it on mobile.' },
					{ kind: 'tool', name: 'browser_show', arg: 'localhost:4321  ·  375 px', done: true },
					{ kind: 'text', spans: [ { s: 'Three options, each under six words. Checking them at 375 px now.' } ] },
				],
			};
		const items = [];
		const a = x => E.outCubic( seg( t, x, x + 0.3 ) );
		if ( t >= T.send )
			items.push( { kind: 'user', text: PROMPT, a: a( T.send ) } );
		for ( const [ at, name, arg ] of TOOLS )
			if ( t >= at )
				items.push( { kind: 'tool', name, arg, done: t >= at + 0.25, a: a( at ) } );
		if ( t >= T.reply )
			items.push( { kind: 'text', spans: REPLY, words: ( t - T.reply ) * 18 } );
		const busy = t >= T.send && t < T.done;
		return {
			title: 'Pricing page speed', items, busy, context: '21%',
			working: busy ? { label: 'Working…', secs: t - T.send } : null,
			prompt: t >= T.type && t < T.send ? { text: PROMPT.slice( 0, Math.floor( ( t - T.type ) * 52 ) ), caret: true, focus: true } : null,
			branch: t >= T.worktree + 0.25 ? 'perf/pricing' : 'main',
			branchFlash: seg( t, T.worktree + 0.25, T.worktree + 0.4 ) * ( 1 - seg( t, T.worktree + 1.2, T.worktree + 1.8 ) ),
			diff: t >= T.report ? [ 48, 6 ] : null,
		};
	}

	function drawWindowAt( t, pal )
	{
		const panels = panelsAt( t );
		const report = t >= T.report + 0.2;
		UI.window( pal, t, {
			sidebar: {
				active: t < SWITCH ? 'hero' : 'pricing',
				working: { hero: 1, drift: 1, notes: 1, camera: 1, pricing: t >= T.send && t < T.done ? 1 : 0 },
				unread: { login: seg( t, 1.6, 1.9 ), pricing: seg( t, T.done, T.done + 0.3 ) },
				hover: { id: 'pricing', a: seg( t, 4.1, 4.3 ) },
			},
			panels,
			draw: {
				chat: r => UI.chat( r, pal, t, chatState( t ) ),
				term: r => L.terminal( UI.tabs( r, pal, 'Terminal', 'term' ), pal, t, PT.TERM_PRICING, 12.5 ),
				git: r => L.gitGraph( UI.tabs( r, pal, 'Git', 'git' ), pal, t, 17.85 ),
				browser: r =>
				{
					const b = report
						? UI.browser( r, pal, 'Before and after', 'file:///F:/website/.loom/before-after.html', seg( t, T.report + 0.2, T.report + 0.6 ) )
						: UI.browser( r, pal, 'Pricing · Acme', 'localhost:4321/pricing', seg( t, T.browser + 0.25, T.browser + 0.9 ) );
					c.save();
					L.clipR( b.x, b.y, b.w, b.h, 0 );
					if ( report )
						PT.reportPage( b, t, T.report + 0.2 );
					else
						PT.pricingPage( b, t, T.browser + 0.25 );
					c.restore();
				},
			},
			drop: { r: DRAG.to, a: seg( t, DRAG.t0 + 0.3, DRAG.t0 + 0.45 ) * ( 1 - seg( t, DRAG.t1 - 0.05, DRAG.t1 + 0.1 ) ) },
			workspace: { name: 'Review', a: seg( t, 21.25, 21.6 ) },
		} );
		return panels;
	}

	/** Pointers inside the window: the sidebar click, then the panel drag. */
	function windowPointers( t )
	{
		const row = UI.sidebarRow( 'pricing' );
		guide( t, [ [ 3.6, 420, 560 ], [ 4.35, row[ 0 ] + 10, row[ 1 ] ] ], SWITCH - 0.05, 4.95 );
		if ( t >= 18.7 && t < 20.8 )
		{
			const head = r => { const q = UI.rect( r ); return [ q.x + 70, q.y + 20 ]; };
			const a = head( DRAG.from ), b = head( DRAG.to );
			const p = t < DRAG.t0 ? PT.pointAt( [ [ 18.7, 1000, 700 ], [ DRAG.t0 - 0.05, a[ 0 ], a[ 1 ] ] ], t )
				: t < DRAG.t1 ? [ lerp( a[ 0 ], b[ 0 ], E.inOutCubic( seg( t, DRAG.t0 + 0.15, DRAG.t1 - 0.05 ) ) ), lerp( a[ 1 ], b[ 1 ], E.inOutCubic( seg( t, DRAG.t0 + 0.15, DRAG.t1 - 0.05 ) ) ) ]
				: PT.pointAt( [ [ DRAG.t1, b[ 0 ], b[ 1 ] ], [ 20.8, b[ 0 ] + 160, b[ 1 ] + 120 ] ], t );
			pointer( p[ 0 ], p[ 1 ], seg( t, 18.7, 18.85 ) * ( 1 - seg( t, 20.4, 20.7 ) ), DRAG.t0, t );
		}
	}

	function sWindow( t )
	{
		if ( t >= 24.3 )
			return;
		const cam = S.camAt( CAM_WIN, t );
		const reveal = E.inOutCubic( seg( t, 4.9, 6.1 ) );
		// The window fades out past a right edge so the words keep paper beside the close-ups:
		// first just past the sidebar, then at a fixed screen edge, released when the camera pulls back.
		const edge = lerp( 1430, 4000, E.inCubic( seg( t, 15.4, 16.4 ) ) );
		const crop = Math.min( lerp( 360, 1440, reveal ), cam.fx + ( edge - cam.sx ) / cam.zoom );
		const fadeW = 110 / Math.max( 1, cam.zoom * 0.8 );
		const l = S.layer( 'win', 1440, 810, t < 16.5 ? 2.1 : 1.3 );
		let panels = null;
		inLayer( l, () =>
		{
			panels = drawWindowAt( t, P );
			windowPointers( t );
			if ( crop < 1440 )
			{
				const g = l.ctx.createLinearGradient( crop - fadeW, 0, crop, 0 );
				g.addColorStop( 0, 'rgba(0,0,0,0)' );
				g.addColorStop( 1, 'rgba(0,0,0,1)' );
				l.ctx.globalCompositeOperation = 'destination-out';
				l.ctx.fillStyle = g;
				l.ctx.fillRect( crop - fadeW, 0, 1440 - crop + fadeW, 810 );
				l.ctx.globalCompositeOperation = 'source-over';
			}
		} );
		const a = ease( seg( t, 0, 0.6 ) ) * ( 1 - E.inCubic( seg( t, 23.7, 24.3 ) ) );
		S.screen( c, l, cam, { alpha: a, shadow: reveal } );

		// Rough arrows from the words to the panels the agent opened.
		const at = ( id, u, v ) =>
		{
			const p = panels.find( q => q.id === id ), r = UI.rect( p.r );
			return S.project( cam, r.x + r.w * u, r.y + r.h * v );
		};
		const fade = ( a0, a1 ) => seg( t, a0, a0 + 0.2 ) * ( 1 - seg( t, a1, a1 + 0.3 ) );
		if ( t > T.terminal + 0.5 && t < 10.6 )
		{
			const q = at( 'term', 0.86, 0.32 );
			arrow( 'a-term', [ 1440, 470 ], [ q[ 0 ] + 30, q[ 1 ] + 14 ], seg( t, T.terminal + 0.6, T.terminal + 1.2 ), fade( T.terminal + 0.5, 10.2 ), 0.22, 3 );
		}
		if ( t > T.browser + 0.5 && t < 12.7 )
		{
			const q = at( 'browser', 0.86, 0.45 );
			arrow( 'a-browser', [ 1440, 610 ], [ q[ 0 ] + 30, q[ 1 ] ], seg( t, T.browser + 0.6, T.browser + 1.2 ), fade( T.browser + 0.5, 12.3 ), -0.2, 7 );
		}

		// Words.
		const until = b => ( { out: t >= b ? t - b : null } );
		pill( 'Built for Omp', 1340, 380, ease( seg( t, 0.6, 1.1 ) ) * ( 1 - seg( t, 4.0, 4.35 ) ) );
		say( [ 'Run every agent', 'from one place' ], 1340, 530, 80, t - 0.8, until( 4.0 ) );
		say( [ 'Your agent', 'uses the app' ], 1640, 540, 68, t - 5.9, until( 8.2 ) );
		say( [ 'It opens', 'a terminal,' ], 1640, 540, 68, t - 8.6, until( 10.2 ) );
		say( [ 'drives', 'the browser,' ], 1640, 540, 68, t - 10.6, until( 12.3 ) );
		say( [ 'and shows you', 'the result.' ], 1640, 540, 68, t - 12.7, until( 15.7 ) );
		say( [ 'In your', 'own layout.' ], 1640, 540, 68, t - 15.9, until( 17.3 ) );
		const out = t >= 23.4 ? t - 23.4 : null;
		say( [ 'Split it.' ], 1660, 430, 72, t - 17.7, { out } );
		say( [ 'Dock it.' ], 1660, 530, 72, t - 19.2, { out } );
		say( [ 'Switch it', 'in one key.' ], 1660, 660, 72, t - 20.8, { out } );
		keys( [ 'Ctrl', 'Alt', '→' ], 760, 990, t - 20.85, 0.8 );
	}

	/* ======================================================================
	   4. Modules: the tile grid, your own module, the community mosaic (24 – 36)
	   ====================================================================== */

	const GRID = { cols: 6, w: 236, h: 112, gap: 18, y0: 420 };
	const GRID_CY = GRID.y0 + ( 3 * GRID.h + 2 * GRID.gap ) / 2;
	/** Tiles switched off in the "turn any of them off" beat: index → time. */
	const OFF = { 11: 26.4, 16: 26.55, 17: 26.7 };
	function tileRect( i )
	{
		const col = i % GRID.cols, row = Math.floor( i / GRID.cols );
		const total = GRID.cols * GRID.w + ( GRID.cols - 1 ) * GRID.gap;
		return { x: ( W - total ) / 2 + col * ( GRID.w + GRID.gap ), y: GRID.y0 + row * ( GRID.h + GRID.gap ), col, row };
	}

	function boardTiles( t )
	{
		if ( t < 23.9 || t >= 28.2 )
			return;
		const part = E.outCubic( seg( t, 27.4, 28.0 ) );
		PT.TILES.forEach( ( tl, i ) =>
		{
			const r = tileRect( i );
			const d = 24.05 + ( r.col + r.row ) * 0.06;
			const p = seg( t, d, d + 0.55 );
			if ( p <= 0 )
				return;
			const dx = r.x + GRID.w / 2 - W / 2, dy = r.y + GRID.h / 2 - GRID_CY;
			const x = r.x + dx * part * 1.5, y = r.y + ( 1 - E.outBack( p ) ) * 70 + dy * part * 1.5;
			let on = seg( t, 24.55 + r.col * 0.07, 24.75 + r.col * 0.07 );
			if ( OFF[ i ] )
				on *= 1 - seg( t, OFF[ i ], OFF[ i ] + 0.18 );
			L.alpha( clamp( p * 2 ) * ( 1 - part ), () => PT.moduleTile( x, y, GRID.w, GRID.h, tl, on, P ) );
		} );
	}

	/** The dashed tile a new module starts from. */
	function yourTile( x, y, w, h )
	{
		L.fillR( x, y, w, h, 12, P.panel, 0.9 );
		c.save();
		c.setLineDash( [ 7, 6 ] );
		L.strokeR( x + 1, y + 1, w - 2, h - 2, 12, P.muted, 1.5 );
		c.restore();
		L.icon( 'plus', x + w / 2 - 11, y + h / 2 - 28, 22, P.text );
		L.txt( 'Your module', x + w / 2, y + h / 2 + 26, 16, P.text, { w: 600, align: 'center' } );
	}

	const MOS = { cols: 4, w: 396, h: 211, gap: 28, pitch: 273, y0: 200 };
	function cellRect( i )
	{
		const col = i % MOS.cols, row = Math.floor( i / MOS.cols );
		const total = MOS.cols * MOS.w + ( MOS.cols - 1 ) * MOS.gap;
		return { x: ( W - total ) / 2 + col * ( MOS.w + MOS.gap ), y: MOS.y0 + row * MOS.pitch, w: MOS.w, h: MOS.h };
	}
	const EDITOR_RECT = { x: 370, y: 190, w: 1180, h: 629 };
	const YOURS = PT.MONTAGE.length - 1;
	const lerpRect = ( a, b, p ) => ( { x: lerp( a.x, b.x, p ), y: lerp( a.y, b.y, p ), w: lerp( a.w, b.w, p ), h: lerp( a.h, b.h, p ) } );

	/** The module editor drawn into r (kept at the editor's 1200x640 aspect, centred on r). */
	function editorIn( r, t )
	{
		const k = r.w / 1200;
		c.save();
		c.translate( r.x, r.y + r.h / 2 - 320 * k );
		c.scale( k, k );
		PT.drawEditor( t + 4.9, { x: 0, y: 0, w: 1200, h: 640 } );
		c.restore();
	}

	function boardEditor( t )
	{
		if ( t < 27.6 || t >= 31.45 )
			return;
		const pop = seg( t, 27.7, 28.0 ), grow = E.inOutCubic( seg( t, 28.0, 28.5 ) ), exit = E.inCubic( seg( t, FLASH.t0, FLASH.t0 + 0.25 ) );
		const tileR = { x: W / 2 - GRID.w / 2, y: GRID_CY - GRID.h / 2, w: GRID.w, h: GRID.h };
		const r = lerpRect( tileR, EDITOR_RECT, grow );
		r.x -= exit * 260;
		if ( grow < 1 )
			L.alpha( clamp( pop * 2 ) * ( 1 - grow ), () => L.scaled( lerp( 0.7, 1, E.outBack( pop ) ), W / 2, GRID_CY, () => yourTile( r.x, r.y, r.w, r.h ) ) );
		if ( grow > 0 )
			L.alpha( grow * ( 1 - exit ), () => editorIn( r, t ) );
	}

	/** A community module card in r: its panel at 1200x640, scaled down. */
	function moduleCard( r, m, lt, t )
	{
		const k = r.w / 1200;
		c.save();
		c.translate( r.x, r.y );
		c.scale( k, k );
		const cr = L.panel( 0, 0, 1200, 640, P, { title: m[ 0 ], icon: m[ 1 ], right: 'modules/' + m[ 2 ], shadowBlur: 50, shadowY: 18, shadowA: 0.4, r: 18 } );
		c.save();
		L.clipR( cr.x, cr.y, cr.w, cr.h, 0 );
		PT.MONTAGE_DRAW[ m[ 2 ] ]( cr, lt, t );
		c.restore();
		c.restore();
	}

	/** Community modules flash one per half beat, each sliding in over the last; the last one lands in the mosaic. */
	const FLASH = { t0: 31.2, step: 0.3, n: YOURS };
	const FLASH_END = FLASH.t0 + FLASH.n * FLASH.step;
	const CARD = { x: 360, y: 180, w: 1200, h: 640 };
	const flashIndex = t => Math.min( FLASH.n - 1, Math.floor( ( t - FLASH.t0 ) / FLASH.step ) );
	function boardFlash( t )
	{
		if ( t < FLASH.t0 || t >= FLASH_END + 0.4 )
			return;
		const k = flashIndex( t );
		for ( const j of [ k - 1, k ] )
		{
			if ( j < 0 || ( j < k && t >= FLASH_END ) )
				continue;
			const start = FLASH.t0 + j * FLASH.step, cur = j === k;
			const p = E.outCubic( seg( t, start, start + 0.14 ) );
			let r = { x: cur ? CARD.x + ( 1 - p ) * 180 : CARD.x - 70, y: CARD.y, w: CARD.w, h: CARD.h };
			if ( cur && t >= FLASH_END )
				r = lerpRect( CARD, cellRect( k ), E.inOutCubic( seg( t, FLASH_END, FLASH_END + 0.4 ) ) );
			const s = cur ? lerp( 0.95, 1, p ) : 0.94;
			L.alpha( cur ? clamp( p * 2 ) : 0.55, () => L.scaled( s, r.x + r.w / 2, r.y + r.h / 2, () => moduleCard( r, PT.MONTAGE[ j ], t - start, t ) ) );
		}
	}

	function boardMosaic( t )
	{
		if ( t < FLASH_END || t >= 36.3 )
			return;
		PT.MONTAGE.forEach( ( m, i ) =>
		{
			const r = cellRect( i ), at = FLASH_END + i * 0.03, landed = i === FLASH.n - 1;
			if ( landed && t < FLASH_END + 0.4 )
				return;
			const p = landed ? 1 : seg( t, at, at + 0.35 );
			if ( p <= 0 )
				return;
			L.alpha( clamp( p * 2 ), () => L.scaled( lerp( 1.2, 1, E.outCubic( p ) ), r.x + r.w / 2, r.y + r.h / 2, () =>
			{
				if ( i === YOURS )
					editorIn( r, t );
				else
					moduleCard( r, m, t - FLASH.t0, t );
			} ) );
		} );
	}

	/** Camera over the modules board (frame units; zoom 1 at fx 960, fy 540 shows it as laid out). */
	const CAM_BOARD = [
		[ 24.0, { fx: 960, fy: 540, sx: 960, sy: 560, zoom: 1.0 } ],
		[ 25.9, { fx: 960, fy: 540, sx: 960, sy: 550, zoom: 1.03 } ],
		[ 26.3, { fx: 1470, fy: 620, sx: 1100, sy: 690, zoom: 1.7 } ],
		[ 27.2, { fx: 1470, fy: 620, sx: 1100, sy: 690, zoom: 1.75 } ],
		[ 27.7, { fx: 960, fy: 540, sx: 960, sy: 540, zoom: 1.0 } ],
		[ 28.4, { fx: 960, fy: 505, sx: 960, sy: 480, zoom: 1.15 } ],
		[ 30.9, { fx: 960, fy: 505, sx: 960, sy: 480, zoom: 1.18 } ],
		[ 31.25, { fx: 960, fy: 540, sx: 960, sy: 540, zoom: 1.0 } ],
		[ 34.6, { fx: 960, fy: 540, sx: 960, sy: 540, zoom: 1.0 } ],
		[ 36.3, { fx: 960, fy: 500, sx: 960, sy: 520, zoom: 1.06 } ],
	];

	function sModules( t )
	{
		if ( t < 23.9 || t >= 36.3 )
			return;
		const cam = S.camAt( CAM_BOARD, t );
		const l = S.layer( 'board', 1920, 1080, 1.7 );
		inLayer( l, () =>
		{
			boardTiles( t );
			boardEditor( t );
			boardFlash( t );
			boardMosaic( t );
		} );
		const a = 1 - E.inCubic( seg( t, 35.7, 36.3 ) );
		S.screen( c, l, cam, { alpha: a, shadow: 0 } );
		// Labels sit on the paper, under the projected cards: the flashing card's name and count, then the mosaic.
		const label = ( text, u, v, size, o ) =>
		{
			const p = S.project( cam, u, v );
			L.txt( text, p[ 0 ], p[ 1 ], size * cam.zoom, INK, o );
		};
		if ( t >= FLASH.t0 && t < FLASH_END )
		{
			const k = flashIndex( t ), start = FLASH.t0 + k * FLASH.step;
			const a2 = clamp( seg( t, start, start + 0.1 ) * 2 );
			label( PT.MONTAGE[ k ][ 0 ], CARD.x + 4, CARD.y + CARD.h + 58, 40, { f: SERIF, w: 500, a: a2 } );
			label( `${ String( k + 1 ).padStart( 2, '0' ) } / ${ FLASH.n }`, CARD.x + CARD.w, CARD.y + CARD.h + 54, 20, { f: L.FM, align: 'right', a: 0.7 } );
		}
		if ( t >= FLASH_END )
			L.alpha( 1 - E.inCubic( seg( t, 35.7, 36.3 ) ), () => PT.MONTAGE.forEach( ( m, i ) =>
			{
				const r = cellRect( i ), at = FLASH_END + 0.2 + i * 0.03;
				label( i === YOURS ? 'Notes, yours' : m[ 0 ], r.x + 4, r.y + r.h + 36, 24, { f: SERIF, w: 500, a: ease( seg( t, at, at + 0.35 ) ) } );
			} ) );
		say( [ 'Every feature is a module' ], W / 2, 250, 72, t - 24.2, { out: t >= 26.0 ? t - 26.0 : null } );
		say( [ 'Turn any of them off' ], W / 2, 250, 72, t - 26.15, { out: t >= 27.4 ? t - 27.4 : null } );
		say( [ 'Write your own. It reloads on save.' ], W / 2, 950, 46, t - 28.6, { out: t >= 31.0 ? t - 31.0 : null, stagger: 0.05 } );
		say( [ 'Or install one from the community' ], W / 2, 110, 56, t - 31.4, { out: t >= 35.5 ? t - 35.5 : null } );
	}

	/* ======================================================================
	   5. From anywhere: the phone client (36 – 42.6)
	   ====================================================================== */

	const CAM_PHONE = [
		[ 35.8, { fx: 115, fy: 220, sx: 560, sy: 560, zoom: 2.0 } ],
		[ 39.6, { fx: 115, fy: 220, sx: 560, sy: 550, zoom: 2.1 } ],
		[ 40.2, { fx: 115, fy: 110, sx: 560, sy: 430, zoom: 2.9 } ],
		[ 42.6, { fx: 115, fy: 120, sx: 560, sy: 440, zoom: 3.0 } ],
	];

	function sPhone( t )
	{
		if ( t < 35.7 || t >= 42.9 )
			return;
		const l = S.layer( 'phone', 230, 440, 4 );
		inLayer( l, () =>
		{
			c.translate( -170, -345 );
			L.fillR( 175, 350, 210, 420, 34, '#151514' );
			L.strokeR( 175.5, 350.5, 209, 419, 34, '#3a3a36', 1 );
			PT.phoneScreen( t + 6.0 );
		} );
		const i = ease( seg( t, 35.8, 36.4 ) ), o = E.inCubic( seg( t, 42.3, 42.9 ) );
		const cam = S.camAt( CAM_PHONE, t );
		S.screen( c, l, Object.assign( {}, cam, { sy: cam.sy + ( 1 - i ) * 60 } ), { alpha: i * ( 1 - o ) } );
		say( [ 'Your PC runs', 'the agents.' ], 1000, 540, 84, t - 36.3, { out: t >= 37.9 ? t - 37.9 : null, align: 'left' } );
		say( [ 'Watch them', 'from anywhere.' ], 1000, 540, 84, t - 38.1, { out: t >= 39.7 ? t - 39.7 : null, align: 'left' } );
		say( [ 'Answer from', 'your phone.' ], 1000, 540, 84, t - 40.0, { out: t >= 42.2 ? t - 42.2 : null, align: 'left' } );
	}

	/* ======================================================================
	   6. Themes (42.6 – 47.4)
	   ====================================================================== */

	const SWAPS = [ 43.5, 44.1, 44.7, 45.3, 45.9 ];
	function sThemes( t )
	{
		if ( t < 42.4 || t >= 47.5 )
			return;
		let pal = L.THEMES[ 0 ];
		SWAPS.forEach( ( s, k ) =>
		{
			const p = seg( t, s, s + 0.18 );
			if ( p > 0 )
				pal = L.mixPal( pal, L.THEMES[ ( k + 1 ) % L.THEMES.length ], p );
		} );
		const l = S.layer( 'win', 1440, 810, 1.3 );
		inLayer( l, () => drawWindowAt( 23.0, pal ) );
		const i = ease( seg( t, 42.5, 43.0 ) ), o = E.inCubic( seg( t, 46.9, 47.4 ) );
		const u = E.inOutSine( seg( t, 42.5, 47.4 ) );
		const cam = { fx: 720, fy: 405, sx: 960, sy: 610, zoom: lerp( 0.78, 0.86, u ) };
		S.screen( c, l, cam, { alpha: i * ( 1 - o ) } );
		say( [ 'Loom’s colors, or a hundred more' ], W / 2, 140, 60, t - 42.9, { out: t >= 46.8 ? t - 46.8 : null } );
	}

	/* ======================================================================
	   7. End card (47.4 – end)
	   ====================================================================== */

	function sEnd( t )
	{
		if ( t < 47.3 )
			return;
		say( [ 'Every agent,', 'woven into one workspace.' ], W / 2, 540, 92, t - 47.6, { out: t >= 49.4 ? t - 49.4 : null } );
		const L0 = TL.LOGO;
		if ( t < L0 - 0.1 )
			return;
		const a = ease( seg( t, L0, L0 + 0.6 ) );
		const ws = L.measure( 'Loom', 120, 500, SERIF, -2.4 );
		const total = 120 + 34 + ws, x0 = W / 2 - total / 2;
		L.alpha( a, () =>
		{
			tile( x0 + 60, 500, 120 );
			L.txt( 'Loom', x0 + 154, 542 + ( 1 - a ) * 10, 120, INK, { f: SERIF, w: 500, ls: -2.4 } );
		} );
		pen( 'end', [ [ x0 + 150, 592 ], [ x0 + 240, 620 ], [ x0 + 330, 580 ], [ x0 + 420, 610 ], [ x0 + total + 30, 576 ] ], seg( t, L0 + 0.4, L0 + 1.4 ), 1, 3.2, 9 );
		pill( 'Built for Omp  ·  Windows 11', W / 2, 720, ease( seg( t, L0 + 0.9, L0 + 1.4 ) ) );
		L.alpha( ease( seg( t, L0 + 1.4, L0 + 2.0 ) ), () => L.txt( 'github.com/antopilo/Loom', W / 2, 810, 28, '#3d4540', { align: 'center' } ) );
	}

	/* ======================================================================
	   Frame
	   ====================================================================== */

	function frame( ctx, t )
	{
		c = ctx;
		useCtx( ctx );
		ctx.save();
		ctx.setTransform( 1, 0, 0, 1, 0, 0 );
		ctx.globalAlpha = 1;
		ctx.globalCompositeOperation = 'source-over';
		ctx.filter = 'none';
		S.paper( ctx, BG );
		sWindow( t );
		sModules( t );
		sPhone( t );
		sThemes( t );
		sEnd( t );
		const fade = seg( t, TL.DURATION - 1.0, TL.DURATION );
		if ( fade > 0 )
		{
			ctx.globalAlpha = fade;
			S.paper( ctx, BG );
			ctx.globalAlpha = 1;
		}
		S.grain( ctx, t, 0.05 );
		ctx.restore();
	}

	root.LoomTrailer = { frame, W, H, DURATION: TL.DURATION, FPS: TL.FPS };
} )( typeof window !== 'undefined' ? window : globalThis );
