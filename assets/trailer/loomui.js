// Loom's real window, drawn from captures of the app: title bar, sidebar, docked panels with tab strips,
// the chat column with its prompt box and status row, terminal, Git and browser panels.
// Everything draws at the origin in window units (1440 x 810) on the context set with LK.use.
( function ( root )
{
	'use strict';

	const L = root.LK;
	const { FM, clamp, seg, lerp, E } = L;
	let c = null;
	const use = ctx => { c = ctx; };

	const WIN = { w: 1440, h: 810, side: 252, top: 42, gap: 8 };

	/* ---------- sidebar ---------- */

	const SIDEBAR = [
		{ name: 'website', color: 'success', chats: [ [ 'pricing', 'Pricing page speed' ], [ 'hero', 'Hero copy pass' ], [ 'footer', 'Fix the footer links' ] ] },
		{ name: 'Racer', color: 'blue', chats: [ [ 'drift', 'Add drift to the car' ], [ 'camera', 'Tune the chase camera' ] ] },
		{ name: 'api', color: 'yellow', chats: [ [ 'login', 'Fix the flaky login test' ], [ 'rate', 'Rate limits for search' ] ] },
		{ scratch: true, name: 'Scratch', chats: [ [ 'notes', 'Write the release notes' ], [ 'blank', 'New chat' ] ] },
	];

	/** Sidebar row position of chat id, in window units (for pointers). */
	function sidebarRow( id )
	{
		let y = WIN.top + 214;
		for ( const p of SIDEBAR )
		{
			y += 30;
			for ( const [ cid ] of p.chats )
			{
				if ( cid === id )
					return [ 120, y + 13 ];
				y += 28;
			}
			y += 12;
		}
		return [ 120, y ];
	}

	/** s: { active, working: { id: true }, unread: { id: true }, hover: { id, a } } */
	function sidebar( P, t, s )
	{
		s = s || {};
		const x = 20;
		let y = WIN.top + 14;
		L.dot( x + 3, y + 9, 4, P.success );
		L.txt( 'This PC', x + 14, y + 14, 13.5, P.text, { w: 600 } );
		L.icon( 'chev', x + 14 + L.measure( 'This PC', 13.5, 600 ) + 2, y + 2, 14, P.muted );
		y += 30;
		for ( const [ ic, label ] of [ [ 'plus', 'New chat' ], [ 'search', 'Search' ], [ 'folder', 'New project' ], [ 'folder', 'Files' ], [ 'bell', 'Notifications' ] ] )
		{
			L.icon( ic, x, y + 4, 15, P.muted );
			L.txt( label, x + 24, y + 16, 13, P.text );
			y += 28;
		}
		y += 30;
		for ( const p of SIDEBAR )
		{
			if ( p.scratch )
				L.txt( p.name, x - 4, y + 16, 12.5, P.muted );
			else
			{
				L.fillR( x, y + 4, 16, 16, 4, P[ p.color ] );
				L.txt( p.name[ 0 ].toUpperCase(), x + 8, y + 16, 10, P.canvas, { w: 700, align: 'center' } );
				L.txt( p.name, x + 24, y + 17, 13.5, P.text, { w: 600 } );
				L.icon( 'chevDown', x + 28 + L.measure( p.name, 13.5, 600 ), y + 7, 11, P.muted );
			}
			y += 30;
			for ( const [ id, title ] of p.chats )
			{
				const hover = s.hover && s.hover.id === id ? s.hover.a : 0;
				if ( id === s.active || hover > 0 )
					L.fillR( 10, y, WIN.side - 22, 26, 6, P.hover, id === s.active ? 1 : hover );
				L.icon( 'chat', x, y + 6, 14, P.muted );
				const working = s.working && s.working[ id ];
				c.save();
				L.clipR( x + 22, y, WIN.side - 80, 26, 0 );
				L.txt( title, x + 22, y + 17.5, 13, P.text );
				c.restore();
				if ( s.unread && s.unread[ id ] )
				{
					const k = clamp( s.unread[ id ] );
					L.dot( x + 30 + L.measure( title, 13 ), y + 13, 3.5 * E.outBack( k ), P.accent );
				}
				if ( working )
					L.stitch( WIN.side - 52, y + 13, 24, t + id.length * 0.31, P.accent, 2.2 );
				y += 28;
			}
			y += 12;
		}
		// Profile avatar and status, bottom left.
		const g = c.createLinearGradient( 16, WIN.h - 44, 36, WIN.h - 24 );
		g.addColorStop( 0, '#7aa8d8' );
		g.addColorStop( 1, '#3f5f8f' );
		c.fillStyle = g;
		L.rrect( 16, WIN.h - 44, 22, 22, 6 );
		c.fill();
		L.icon( 'sun', 48, WIN.h - 41, 15, P.success );
	}

	/* ---------- window frame ---------- */

	function chrome( P )
	{
		const g = c.createLinearGradient( 0, 0, 0, WIN.h );
		g.addColorStop( 0, L.css( L.mix( P.canvas, [ 0, 0, 0 ], 0.22 ) ) );
		g.addColorStop( 0.75, L.css( L.mix( P.canvas, [ 0, 0, 0 ], 0.18 ) ) );
		g.addColorStop( 1, L.css( L.mix( P.canvas, [ 60, 22, 34 ], 0.3 ) ) );
		c.fillStyle = g;
		c.fillRect( 0, 0, WIN.w, WIN.h );
		// Sidebar toggle and settings.
		L.strokeR( 18.5, 15.5, 15, 12, 2.5, P.muted, 1.3 );
		L.line( 23.5, 16, 23.5, 27, P.muted, 1.3 );
		L.icon( 'gear', 44, 13, 16, P.muted );
		// Window controls.
		const bx = WIN.w - 134;
		L.line( bx, 21.5, bx + 10, 21.5, P.muted, 1.1 );
		L.strokeR( bx + 44.5, 16.5, 10, 10, 1.5, P.muted, 1.1 );
		L.line( bx + 90, 16, bx + 100, 26, P.muted, 1.1 );
		L.line( bx + 100, 16, bx + 90, 26, P.muted, 1.1 );
	}

	/** The area the docked panels share. */
	function area()
	{
		return { x: WIN.side + 4, y: WIN.top, w: WIN.w - WIN.side - 12, h: WIN.h - WIN.top - 8 };
	}

	/** Rectangle of normalized rect r = [ nx, ny, nw, nh ] inside the panel area. */
	function rect( r )
	{
		const A = area(), g = WIN.gap / 2;
		return { x: A.x + r[ 0 ] * A.w + g, y: A.y + r[ 1 ] * A.h + g, w: r[ 2 ] * A.w - 2 * g, h: r[ 3 ] * A.h - 2 * g };
	}

	/** A docked panel frame; returns its rectangle. */
	function frame( r, P, active )
	{
		L.fillR( r.x, r.y, r.w, r.h, 12, P.panel );
		L.strokeR( r.x + 0.5, r.y + 0.5, r.w - 1, r.h - 1, 12, active ? P.accent : P.border, 1, active ? 0.75 : 1 );
		return r;
	}

	/** One tab strip: the tab, its close cross, then + and the tab list. Returns the area below. */
	function tabs( r, P, title, ic, extra )
	{
		const tw = Math.min( r.w - 90, L.measure( title, 12.5 ) + ( ic ? 62 : 44 ) );
		L.fillR( r.x + 8, r.y + 7, tw, 26, 6, P.hover );
		let tx = r.x + 18;
		if ( ic )
		{
			L.icon( ic, tx, r.y + 12, 14, P.muted );
			tx += 20;
		}
		c.save();
		L.clipR( tx, r.y + 7, tw - ( tx - r.x - 8 ) - 24, 26, 0 );
		L.txt( title, tx, r.y + 24.5, 12.5, P.text );
		c.restore();
		const cx = r.x + 8 + tw - 15;
		L.line( cx - 3.5, r.y + 16.5, cx + 3.5, r.y + 23.5, P.muted, 1.1 );
		L.line( cx + 3.5, r.y + 16.5, cx - 3.5, r.y + 23.5, P.muted, 1.1 );
		if ( extra !== false )
		{
			L.icon( 'plus', r.x + tw + 20, r.y + 13, 14, P.muted );
			for ( let i = 0; i < 3; i++ )
			{
				L.dot( r.x + tw + 47, r.y + 16 + i * 4, 1, P.muted );
				L.line( r.x + tw + 50, r.y + 16 + i * 4, r.x + tw + 58, r.y + 16 + i * 4, P.muted, 1.1 );
			}
		}
		return { x: r.x, y: r.y + 40, w: r.w, h: r.h - 40 };
	}

	/** Browser chrome inside a panel: tab strip, then back, forward, reload and the address field. Returns the page area. */
	function browser( r, P, title, url, loading )
	{
		const b = tabs( r, P, title, null );
		const y = b.y + 2;
		L.icon( 'chev', b.x + 10, y + 6, 14, P.muted );
		c.save();
		c.translate( b.x + 17, y + 13 );
		c.scale( -1, 1 );
		c.translate( -( b.x + 17 ), -( y + 13 ) );
		L.icon( 'chev', b.x + 10, y + 6, 14, P.muted );
		c.restore();
		L.icon( 'chev', b.x + 32, y + 6, 14, P.inactive );
		L.icon( 'refresh', b.x + 56, y + 6, 14, P.muted );
		L.fillR( b.x + 80, y, b.w - 92, 26, 6, P.canvas );
		L.strokeR( b.x + 80.5, y + 0.5, b.w - 93, 25, 6, P.border );
		c.save();
		L.clipR( b.x + 80, y, b.w - 92, 26, 0 );
		L.txt( url, b.x + 92, y + 17.5, 12.5, P.text );
		c.restore();
		if ( loading != null && loading > 0 && loading < 1 )
			L.fillR( b.x + 80, y + 24, ( b.w - 92 ) * E.outCubic( loading ), 2, 1, P.accent );
		return { x: b.x + 1, y: y + 34, w: b.w - 2, h: b.h - 36 - 1 };
	}

	/* ---------- chat column ---------- */

	/**
	 * Chat panel in the real layout. o: {
	 *   title, items: [ { kind: 'user'|'tool'|'text', ... , a } ], working: { label, secs, prog } | null,
	 *   prompt: { text, caret } | null, branch, branchFlash, diff: [ add, del ], context, busy
	 * }
	 */
	function chat( r, P, t, o )
	{
		c.save();
		L.clipR( r.x, r.y, r.w, r.h, 12 );
		L.txt( o.title, r.x + r.w / 2, r.y + 22, 12, P.muted, { align: 'center' } );
		const colW = Math.min( 700, r.w - 56 ), x0 = r.x + ( r.w - colW ) / 2;
		const promptY = r.y + r.h - 86, top = r.y + 40;
		const bottom = promptY - ( o.working ? 34 : 14 );
		// Measure the stack, then draw it anchored to the bottom like a scrolled chat.
		const heights = o.items.map( it => itemHeight( it, colW ) * ( it.grow ?? 1 ) );
		const total = heights.reduce( ( s, h ) => s + h + 18, 0 );
		let y = Math.min( top + 10, bottom - total );
		c.save();
		L.clipR( r.x, top, r.w, bottom - top, 0 );
		o.items.forEach( ( it, i ) =>
		{
			L.alpha( it.a ?? 1, () => drawItem( it, x0, y + ( 1 - ( it.a ?? 1 ) ) * 8, colW, P, t ) );
			y += heights[ i ] + 18;
		} );
		c.restore();
		if ( o.working )
		{
			const wy = promptY - 26;
			L.stitch( x0 + 2, wy + 2, 18, t, P.accent, 2 );
			L.txt( o.working.label, x0 + 30, wy + 6, 12.5, P.accent );
			L.txt( `${ Math.floor( o.working.secs ) }s`, x0 + 36 + L.measure( o.working.label, 12.5 ), wy + 6, 12.5, P.muted );
			L.fillR( x0 + 2, wy + 15, colW * 0.3, 2, 1, P.muted, 0.6 );
		}
		// Prompt box.
		const pb = { x: x0, y: promptY, w: colW, h: 42 };
		if ( o.context )
		{
			const cw = L.measure( o.context, 11, 700 ) + 34;
			L.fillR( pb.x + pb.w - cw - 6, pb.y - 13, cw, 18, 6, P.canvas );
			L.txt( o.context, pb.x + pb.w - cw + 2, pb.y, 11, P.text, { w: 700 } );
			L.txt( '1M', pb.x + pb.w - 26, pb.y, 11, P.muted );
		}
		L.fillR( pb.x, pb.y, pb.w, pb.h, 12, P.raised );
		L.strokeR( pb.x + 0.5, pb.y + 0.5, pb.w - 1, pb.h - 1, 12, o.prompt && o.prompt.focus ? P.accent : P.border, 1, o.prompt && o.prompt.focus ? 0.7 : 1 );
		L.icon( 'plus', pb.x + 12, pb.y + 14, 14, P.muted );
		if ( o.prompt && o.prompt.text )
		{
			c.save();
			L.clipR( pb.x + 34, pb.y, pb.w - 120, pb.h, 0 );
			const tw = L.measure( o.prompt.text, 13 );
			const sx = Math.min( 0, pb.w - 130 - tw );
			L.txt( o.prompt.text, pb.x + 38 + sx, pb.y + 26, 13, P.text );
			if ( o.prompt.caret )
				L.fillR( pb.x + 39 + sx + tw, pb.y + 12, 1.5, 18, 0, P.accent );
			c.restore();
		}
		else
			L.txt( o.busy ? 'Queue a message… (Ctrl+Enter steers)' : 'Message Loom', pb.x + 38, pb.y + 26, 13, P.muted );
		if ( o.busy )
			L.txt( 'Queue', pb.x + pb.w - 50, pb.y + 26, 12.5, P.muted, { align: 'right' } );
		L.dot( pb.x + pb.w - 22, pb.y + 21, 12, P.accent );
		if ( o.busy )
		{
			L.ring( pb.x + pb.w - 22, pb.y + 21, 8, P.canvas, 1.6 );
			L.fillR( pb.x + pb.w - 25, pb.y + 18, 6, 6, 1, P.canvas );
		}
		else
			L.icon( 'arrowUp', pb.x + pb.w - 30, pb.y + 13, 16, P.canvas );
		// Status row.
		const sy = pb.y + pb.h + 22;
		L.icon( 'branch', pb.x + 6, sy - 11, 13, P.muted );
		const flash = o.branchFlash || 0;
		const bw = L.measure( o.branch || 'main', 12.5 );
		if ( flash > 0 )
			L.fillR( pb.x + 22, sy - 14, bw + 12, 20, 5, P.accent, 0.25 * flash );
		L.txt( o.branch || 'main', pb.x + 28, sy, 12.5, flash > 0 ? L.mix( P.text, P.accent, flash ) : P.text );
		if ( o.diff )
		{
			const dx = pb.x + 46 + bw;
			L.txt( `+${ o.diff[ 0 ] }`, dx, sy, 12.5, P.success );
			L.txt( `−${ o.diff[ 1 ] }`, dx + L.measure( `+${ o.diff[ 0 ] } `, 12.5 ) + 2, sy, 12.5, P.error );
		}
		L.txt( 'Claude Opus 5.5', pb.x + pb.w - 6, sy, 12.5, P.text, { align: 'right' } );
		const mx = pb.x + pb.w - 14 - L.measure( 'Claude Opus 5.5', 12.5 );
		L.ring( mx - 6, sy - 4, 5, P.muted, 1.4 );
		L.txt( 'Usage', mx - 26, sy, 12.5, P.muted, { align: 'right' } );
		c.restore();
	}

	function itemHeight( it, colW )
	{
		if ( it.kind === 'user' )
			return L.wrap( it.text, Math.min( colW * 0.78, 520 ) - 28, 13 ).length * 20 + 20;
		if ( it.kind === 'tool' )
			return 22;
		if ( it.kind === 'text' )
			return L.wrap( it.spans.map( s => s.s ).join( '' ), colW, 13.5 ).length * 22;
		return 0;
	}

	const TOOL_ICON = { worktrees_enter: 'branch', shell_open_panel: 'term', browser_show: 'globe' };
	function drawItem( it, x, y, colW, P, t )
	{
		if ( it.kind === 'user' )
		{
			const lines = L.wrap( it.text, Math.min( colW * 0.78, 520 ) - 28, 13 );
			const w = Math.max( ...lines.map( s => L.measure( s, 13 ) ) ) + 28;
			const bx = x + colW - w;
			L.fillR( bx, y, w, lines.length * 20 + 20, 12, P.raised );
			lines.forEach( ( s, i ) => L.txt( s, bx + 14, y + 24 + i * 20, 13, P.text ) );
		}
		else if ( it.kind === 'tool' )
		{
			if ( it.done )
				L.icon( TOOL_ICON[ it.name ] || 'chev', x, y + 3, 15, P.muted );
			else
				L.spinner( x + 7, y + 11, 6, t, P.accent, 1.6 );
			L.txt( it.name, x + 24, y + 15.5, 12.5, P.text, { f: FM } );
			L.txt( it.arg, x + 34 + L.measure( it.name, 12.5, 400, FM ), y + 15.5, 12.5, P.muted, { f: FM } );
		}
		else if ( it.kind === 'text' )
		{
			// Word wrap that keeps bold spans.
			const words = [];
			it.spans.forEach( sp => sp.s.split( /(\s+)/ ).forEach( w => { if ( w ) words.push( { w, b: sp.b } ); } ) );
			const shown = it.words ?? Infinity;
			let lx = x, ly = y + 16, k = 0;
			for ( const wd of words )
			{
				const ww = L.measure( wd.w, 13.5, wd.b ? 700 : 400 );
				if ( !/\s/.test( wd.w ) && lx + ww > x + colW )
				{
					lx = x;
					ly += 22;
				}
				if ( !/\s/.test( wd.w ) )
				{
					const a = clamp( shown - k++ );
					if ( a > 0 )
						L.txt( wd.w, lx, ly, 13.5, P.text, { w: wd.b ? 700 : 400, a } );
				}
				lx += ww;
			}
		}
	}

	/* ---------- window ---------- */

	/**
	 * The whole window. o: {
	 *   sidebar: see sidebar(), panels: [ { id, r, a, lift, scale, active } ],
	 *   draw: { [id]: ( rect ) => void } panel content callbacks, drop: { r, a }, workspace: { name, a }
	 * }
	 */
	function window_( P, t, o )
	{
		c.save();
		L.clipR( 0, 0, WIN.w, WIN.h, 10 );
		chrome( P );
		sidebar( P, t, o.sidebar );
		if ( o.workspace && o.workspace.a > 0 )
			L.alpha( o.workspace.a, () =>
			{
				const w = L.measure( o.workspace.name, 12.5, 600 ) + 40;
				L.fillR( WIN.w / 2 - w / 2, 10, w, 24, 7, P.raised );
				L.icon( 'grid', WIN.w / 2 - w / 2 + 9, 15, 13, P.accent );
				L.txt( o.workspace.name, WIN.w / 2 - w / 2 + 28, 26.5, 12.5, P.text, { w: 600 } );
			} );
		const order = o.panels.slice().sort( ( a, b ) => ( a.lift || 0 ) - ( b.lift || 0 ) );
		for ( const p of order )
		{
			const r = rect( p.r );
			if ( r.w < 8 || r.h < 8 || ( p.a ?? 1 ) <= 0.01 )
				continue;
			L.alpha( p.a ?? 1, () => L.scaled( p.scale ?? 1, r.x + r.w / 2, r.y + r.h / 2, () =>
			{
				const lift = p.lift || 0;
				if ( lift > 0 )
					L.shadow( 50 * lift, 20 * lift, 0.6 * lift, () => L.fillR( r.x, r.y, r.w, r.h, 12, P.panel ) );
				frame( r, P, p.active );
				c.save();
				L.clipR( r.x, r.y, r.w, r.h, 12 );
				if ( o.draw[ p.id ] )
					o.draw[ p.id ]( r );
				c.restore();
			} ) );
		}
		if ( o.drop && o.drop.a > 0 )
			L.alpha( o.drop.a, () =>
			{
				const r = rect( o.drop.r );
				L.fillR( r.x, r.y, r.w, r.h, 12, P.drop, 0.5 );
				L.strokeR( r.x + 1, r.y + 1, r.w - 2, r.h - 2, 12, P.accent, 2 );
			} );
		c.restore();
		L.strokeR( 0.5, 0.5, WIN.w - 1, WIN.h - 1, 10, P.border );
	}

	root.LoomUI = { use, WIN, area, rect, sidebarRow, tabs, browser, chat, window: window_ };
} )( typeof window !== 'undefined' ? window : globalThis );
