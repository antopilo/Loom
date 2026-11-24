'use strict';

const reduceMotion = matchMedia( '(prefers-reduced-motion: reduce)' ).matches;
const $ = ( s, r = document ) => r.querySelector( s );
const $$ = ( s, r = document ) => [ ...r.querySelectorAll( s ) ];
const sleep = ms => new Promise( r => setTimeout( r, ms ) );

/* ------------------------------------------------------------------
 * Weave backdrop: a ribbon of weft threads sweeps through the lower-left
 * corner, crossed by warp threads that go over and under it. Below the
 * ribbon lies the woven cloth; above it, loose fibres. Half resolution,
 * drawn into one ImageData, so it reads as dithered grain.
 * ------------------------------------------------------------------ */
( () =>
{
	const canvas = $( '#weave' );
	const ctx = canvas.getContext( '2d' );
	const SCALE = 0.5;
	const THREADS = 22;
	const WARP = 5;
	const PALETTE = [ [ 255, 226, 170 ], [ 240, 196, 140 ], [ 213, 165, 131 ], [ 224, 133, 124 ], [ 197, 135, 190 ], [ 150, 110, 196 ] ];
	let W = 0, H = 0, img = null, buf = null, noise = null, dust = null, threadColor = null;

	const mix = ( a, b, t ) => a + ( b - a ) * t;
	const paletteAt = t =>
	{
		const p = Math.min( PALETTE.length - 1.001, Math.max( 0, t * ( PALETTE.length - 1 ) ) );
		const i = Math.floor( p ), f = p - i;
		return PALETTE[ i ].map( ( c, k ) => mix( c, PALETTE[ i + 1 ][ k ], f ) );
	};
	const pack = ( r, g, b, a ) => ( ( Math.max( 0, Math.min( 255, a ) ) & 255 ) << 24 | ( b & 255 ) << 16 | ( g & 255 ) << 8 | ( r & 255 ) ) >>> 0;

	// The ribbon's top edge: enters on the left at a third of the height, leaves through the bottom past the middle.
	const edge = ( x, t ) =>
	{
		const u = x / W;
		return H * ( 0.47 + 1.1 * Math.pow( u, 1.3 ) ) + H * 0.03 * Math.sin( u * 6.5 + t * 0.35 ) + H * 0.012 * Math.sin( u * 17 - t * 0.6 );
	};

	const build = () =>
	{
		W = Math.ceil( innerWidth * SCALE );
		H = Math.ceil( innerHeight * SCALE );
		canvas.width = W;
		canvas.height = H;
		img = ctx.createImageData( W, H );
		buf = new Uint32Array( img.data.buffer );
		noise = new Float32Array( W * H );
		for ( let i = 0; i < noise.length; i++ )
			noise[ i ] = Math.random();
		threadColor = [];
		for ( let k = 0; k < THREADS; k++ )
			threadColor.push( paletteAt( k / ( THREADS - 1 ) ) );

		const count = Math.round( W * H * 0.05 );
		dust = new Float32Array( count * 3 );
		for ( let n = 0; n < count; n++ )
		{
			dust[ n * 3 ] = Math.random() * W;
			dust[ n * 3 + 1 ] = Math.pow( Math.random(), 2.6 ) * H * 0.42;
			dust[ n * 3 + 2 ] = Math.random();
		}
	};

	const draw = t =>
	{
		buf.fill( 0 );
		const spacing = Math.max( 2, Math.round( H * 0.0085 ) );
		const band = spacing * THREADS;
		const xEnd = W;

		for ( let x = 0; x < xEnd; x++ )
		{
			const top = edge( x, t );
			if ( top > H + 4 )
				break;
			const warp = x % WARP === 0;
			const column = ( x / WARP ) | 0;

			// Cloth below the ribbon: a sparse lattice that fades with depth.
			const clothTop = Math.max( 0, Math.ceil( top + band ) );
			for ( let y = clothTop; y < H; y++ )
			{
				const depth = ( y - top - band ) / H;
				const onLattice = warp || y % WARP === 0;
				const p = ( onLattice ? 0.5 : 0.06 ) * Math.exp( -depth * 3 );
				const i = y * W + x;
				if ( noise[ i ] > p )
					continue;
				const over = ( ( column + ( ( y / WARP ) | 0 ) ) & 1 ) === 0;
				const shade = 1 - Math.min( 0.75, depth * 1.4 );
				buf[ i ] = over
					? pack( 186, 120, 176, 200 * shade )
					: pack( 116, 86, 170, 170 * shade );
			}

			// The ribbon: one line per weft thread.
			for ( let k = 0; k < THREADS; k++ )
			{
				const y = Math.round( top + k * spacing + Math.sin( t * 0.9 + k * 0.7 + x * 0.02 ) * 0.6 );
				if ( y < 0 || y >= H )
					continue;
				const i = y * W + x;
				if ( warp && ( ( column + k ) & 1 ) )
					continue;
				const n = noise[ i ];
				if ( n > 0.9 )
					continue;
				const [ r, g, b ] = threadColor[ k ];
				const a = k === 0 ? 255 : 150 + 100 * n;
				buf[ i ] = pack( r, g, b, a );
				if ( k === 0 && y > 0 )
					buf[ i - W ] = pack( 255, 236, 200, 150 );
			}

			// Warp threads crossing the ribbon, visible where they pass over.
			if ( warp )
			{
				const y0 = Math.max( 0, Math.round( top - spacing ) ), y1 = Math.min( H - 1, Math.round( top + band ) );
				for ( let y = y0; y <= y1; y++ )
				{
					const k = Math.round( ( y - top ) / spacing );
					if ( ( ( column + k ) & 1 ) === 0 )
						continue;
					buf[ y * W + x ] = pack( 255, 240, 222, 170 );
				}
			}
		}

		// Loose fibres thrown off the ribbon's top edge.
		for ( let n = 0, len = dust.length / 3; n < len; n++ )
		{
			const x = dust[ n * 3 ] | 0;
			const top = edge( x, t );
			const y = Math.round( top - 3 - dust[ n * 3 + 1 ] );
			if ( y < 0 || y >= H )
				continue;
			const lift = dust[ n * 3 + 1 ] / ( H * 0.42 );
			const tw = 0.55 + 0.45 * Math.sin( t * 1.7 + dust[ n * 3 + 2 ] * 80 );
			buf[ y * W + x ] = pack( 248, 240, 228, ( 230 - lift * 200 ) * tw );
		}

		ctx.putImageData( img, 0, 0 );
	};

	let last = 0, raf = 0;
	const loop = now =>
	{
		raf = requestAnimationFrame( loop );
		if ( now - last < 33 )
			return;
		last = now;
		draw( now / 1000 );
	};

	build();
	addEventListener( 'resize', () => { build(); draw( performance.now() / 1000 ); } );
	if ( reduceMotion )
		draw( 0 );
	else
	{
		raf = requestAnimationFrame( loop );
		document.addEventListener( 'visibilitychange', () =>
		{
			cancelAnimationFrame( raf );
			if ( !document.hidden )
				raf = requestAnimationFrame( loop );
		} );
	}
} )();

/* ------------------------------------------------------------------
 * Chrome: header, progress, rail, reveal, keys.
 * ------------------------------------------------------------------ */
( () =>
{
	// Loom's titlebar: sidebar toggle and settings on the left, the chat's title in the middle, notifications and caption buttons on the right.
	const icon = id => `<svg class="ico"><use href="#${ id }"/></svg>`;
	for ( const bar of $$( '.titlebar[data-title]' ) )
	{
		const compact = bar.dataset.compact;
		const badge = bar.dataset.bell ? `<i>${ bar.dataset.bell }</i>` : '';
		bar.innerHTML =
			`<span class="tb-btn">${ icon( 'i-panel' ) }</span>` +
			( compact ? '' : `<span class="tb-btn">${ icon( 'i-gear' ) }</span>` ) +
			`<span class="tb-title">${ bar.dataset.title }</span><span class="grow"></span>` +
			( compact ? '' : `<span class="tb-btn bell">${ icon( 'i-bell' ) }${ badge }</span>` +
				'<span class="caption"><span><svg><use href="#cap-min"/></svg></span><span><svg><use href="#cap-max"/></svg></span><span><svg><use href="#cap-close"/></svg></span></span>' );
	}

	const top = $( '.top' );
	const progress = $( '.progress' );
	const rail = $$( '.rail a' );
	const sections = rail.map( a => $( a.getAttribute( 'href' ) ) );

	const onScroll = () =>
	{
		const max = document.documentElement.scrollHeight - innerHeight;
		progress.style.transform = `scaleX(${ max > 0 ? scrollY / max : 0 })`;
		top.classList.toggle( 'scrolled', scrollY > 20 );
		let active = -1;
		sections.forEach( ( s, i ) =>
		{
			if ( s.getBoundingClientRect().top < innerHeight * 0.5 )
				active = i;
		} );
		if ( $( '#get' ).getBoundingClientRect().top < innerHeight * 0.5 )
			active = -1;
		rail.forEach( ( a, i ) => a.classList.toggle( 'active', i === active ) );
	};
	addEventListener( 'scroll', onScroll, { passive: true } );
	onScroll();

	const io = new IntersectionObserver( entries =>
	{
		for ( const e of entries )
			if ( e.isIntersecting )
			{
				e.target.classList.add( 'in' );
				io.unobserve( e.target );
			}
	}, { threshold: 0.12, rootMargin: '0px 0px -6% 0px' } );
	$$( '.reveal, .final' ).forEach( el => io.observe( el ) );

	addEventListener( 'keydown', e =>
	{
		if ( e.ctrlKey || e.metaKey || e.altKey || /input|textarea/i.test( e.target.tagName ) )
			return;
		const n = Number( e.key );
		if ( n >= 1 && n <= sections.length )
			sections[ n - 1 ].scrollIntoView( { behavior: reduceMotion ? 'auto' : 'smooth' } );
	} );
} )();

/** Runs `fn` every time `el` comes into view, and aborts its signal when it leaves. */
function whenVisible( el, fn )
{
	let ctrl = null;
	new IntersectionObserver( ( [ e ] ) =>
	{
		if ( e.isIntersecting && !ctrl )
		{
			ctrl = new AbortController();
			fn( ctrl.signal ).catch( () => {} );
		}
		else if ( !e.isIntersecting && ctrl )
		{
			ctrl.abort();
			ctrl = null;
		}
	}, { threshold: 0.25 } ).observe( el );
}

/** A sleep that throws once the signal aborts, so loops stop off screen. */
async function wait( ms, signal )
{
	await sleep( reduceMotion ? 0 : ms );
	if ( signal.aborted )
		throw new Error( 'aborted' );
}

async function typeInto( el, text, signal, speed = 38 )
{
	el.textContent = '';
	for ( const ch of text )
	{
		el.textContent += ch;
		await wait( speed + Math.random() * speed, signal );
	}
}

/* ------------------------------------------------------------------
 * Hero: a check run streams in the terminal, a prompt types itself.
 * ------------------------------------------------------------------ */
( () =>
{
	const term = $( '#hero-term' );
	const type = $( '#hero-type' );
	const lines = [
		'PS F:\\Lokn&gt; pwsh scripts/check.ps1 -Suite chat',
		'<span class="mu">Building Lokn (Debug)…</span>',
		'<span class="mu">  Lokn.Core</span>            <span class="gr">ok</span>',
		'<span class="mu">  Lokn.Modules.Omp</span>      <span class="gr">ok</span>',
		'<span class="mu">  Lokn.Modules.Conversations</span> <span class="gr">ok</span>',
		'<span class="mu">Running suite</span> <span class="bl">chat</span>',
		'  ask form renders       <span class="gr">pass</span>',
		'  ask form submits       <span class="gr">pass</span>',
		'  recommended on Ctrl+Enter <span class="gr">pass</span>',
		'  plan implement/revise  <span class="gr">pass</span>',
		'  goal pause and resume  <span class="gr">pass</span>',
		'<span class="gr">PASSED</span> chat',
	];
	whenVisible( term, async signal =>
	{
		for ( ;; )
		{
			term.innerHTML = '';
			for ( const line of lines )
			{
				term.innerHTML += line + '\n';
				await wait( 280 + Math.random() * 260, signal );
			}
			await wait( 2600, signal );
		}
	} );
	whenVisible( type, async signal =>
	{
		for ( ;; )
		{
			type.innerHTML = '<span class="ph">Queue a message… (Ctrl+Enter steers)</span>';
			await wait( 1800, signal );
			const span = document.createElement( 'span' );
			type.replaceChildren( span, Object.assign( document.createElement( 'span' ), { className: 'caret' } ) );
			await typeInto( span, 'Then open a PR and wait for the checks.', signal, 45 );
			await wait( 3200, signal );
		}
	} );
} )();

/* ------------------------------------------------------------------
 * Film. Drop media/loom-trailer.mp4 (and a .jpg poster) next to this page.
 * ------------------------------------------------------------------ */
( () =>
{
	const frame = $( '#film-frame' );
	const video = $( '#film-video' );
	const button = $( '#film-play' );
	const stamp = $( '.frame-mark.br', frame );
	const missing = () => frame.classList.add( 'missing' );
	video.addEventListener( 'error', missing );
	if ( video.error || video.networkState === HTMLMediaElement.NETWORK_NO_SOURCE )
		missing();
	button.addEventListener( 'click', () =>
	{
		if ( frame.classList.contains( 'missing' ) )
			return;
		video.controls = true;
		frame.classList.add( 'playing' );
		video.play();
	} );
	video.addEventListener( 'ended', () =>
	{
		frame.classList.remove( 'playing' );
		video.controls = false;
	} );
	let f = 0;
	setInterval( () =>
	{
		if ( frame.classList.contains( 'playing' ) )
			return;
		f = ( f + 1 ) % ( 24 * 60 * 60 );
		const s = Math.floor( f / 24 );
		const pad = n => String( n ).padStart( 2, '0' );
		stamp.textContent = `00:${ pad( Math.floor( s / 60 ) ) }:${ pad( s % 60 ) }:${ pad( f % 24 ) }`;
	}, 1000 / 24 );
} )();

/* ------------------------------------------------------------------
 * 02 Chat: the turn plays out, the recommended answer gets picked.
 * ------------------------------------------------------------------ */
( () =>
{
	const demo = $( '#chat-demo' );
	const steps = $$( '.step', demo );
	const rec = $( '#ask-rec' );
	whenVisible( demo, async signal =>
	{
		for ( ;; )
		{
			steps.forEach( s => s.classList.remove( 'show' ) );
			rec.classList.remove( 'picked' );
			await wait( 400, signal );
			for ( const [ i, s ] of steps.entries() )
			{
				s.classList.add( 'show' );
				if ( i === 2 )
				{
					await wait( 1400, signal );
					rec.classList.add( 'picked' );
				}
				await wait( 900, signal );
			}
			await wait( 5000, signal );
		}
	} );
} )();

/* ------------------------------------------------------------------
 * 03 Workspace: saved layouts; panels slide between them.
 * ------------------------------------------------------------------ */
const Dock = ( () =>
{
	const GAP = 6;
	/** Places panes in `root` by fractional rects [x, y, w, h]; panes absent from `layout` fade out. */
	function apply( root, layout )
	{
		const W = root.clientWidth, H = root.clientHeight;
		for ( const pane of $$( ':scope > .pane', root ) )
		{
			const r = layout[ pane.dataset.p ];
			pane.classList.toggle( 'gone', !r );
			if ( !r )
				continue;
			const [ x, y, w, h ] = r;
			pane.style.left = `${ x * W + ( x > 0 ? GAP / 2 : 0 ) }px`;
			pane.style.top = `${ y * H + ( y > 0 ? GAP / 2 : 0 ) }px`;
			pane.style.width = `${ w * W - ( x > 0 ? GAP / 2 : 0 ) - ( x + w < 1 ? GAP / 2 : 0 ) }px`;
			pane.style.height = `${ h * H - ( y > 0 ? GAP / 2 : 0 ) - ( y + h < 1 ? GAP / 2 : 0 ) }px`;
		}
	}
	return { apply };
} )();

( () =>
{
	const dock = $( '#dock' );
	const tiles = $( '#tiles' );
	const layouts = [
		{ name: 'Focus', key: 'Ctrl Alt 1', panes: { chat: [ 0, 0, 0.62, 1 ], term: [ 0.62, 0, 0.38, 0.5 ], git: [ 0.62, 0.5, 0.38, 0.5 ] } },
		{ name: 'Review', key: 'Ctrl Alt 2', panes: { git: [ 0, 0, 0.5, 1 ], chat: [ 0.5, 0, 0.5, 0.6 ], files: [ 0.5, 0.6, 0.5, 0.4 ] } },
		{ name: 'Web', key: 'Ctrl Alt 3', panes: { chat: [ 0, 0, 0.36, 1 ], web: [ 0.36, 0, 0.64, 0.66 ], term: [ 0.36, 0.66, 0.64, 0.34 ] } },
		{ name: 'Everything', key: 'Ctrl Alt 4', panes: { files: [ 0, 0, 0.2, 1 ], chat: [ 0.2, 0, 0.4, 1 ], web: [ 0.6, 0, 0.4, 0.5 ], term: [ 0.6, 0.5, 0.2, 0.5 ], git: [ 0.8, 0.5, 0.2, 0.5 ] } },
	];
	let current = 0, timer = 0;

	for ( const [ i, l ] of layouts.entries() )
	{
		const tile = document.createElement( 'div' );
		tile.className = 'tile';
		const mini = document.createElement( 'div' );
		mini.className = 'mini';
		const order = [ 'chat', 'web', 'git', 'term', 'files' ];
		for ( const key of order.filter( k => l.panes[ k ] ) )
		{
			const [ x, y, w, h ] = l.panes[ key ];
			const r = document.createElement( 'i' );
			Object.assign( r.style, { left: `calc(${ x * 100 }% + 3px)`, top: `calc(${ y * 100 }% + 3px)`, width: `calc(${ w * 100 }% - 6px)`, height: `calc(${ h * 100 }% - 6px)` } );
			mini.append( r );
		}
		const label = document.createElement( 'div' );
		label.className = 'tl';
		label.innerHTML = `<span>${ l.name }</span><kbd>${ i + 1 }</kbd>`;
		tile.append( mini, label );
		tile.addEventListener( 'click', () => { show( i ); restart(); } );
		tiles.append( tile );
	}

	function show( i )
	{
		current = i;
		Dock.apply( dock, layouts[ i ].panes );
		$$( '.tile', tiles ).forEach( ( t, k ) => t.classList.toggle( 'on', k === i ) );
	}
	function restart()
	{
		clearInterval( timer );
		if ( !reduceMotion )
			timer = setInterval( () => show( ( current + 1 ) % layouts.length ), 3200 );
	}
	show( 0 );
	addEventListener( 'resize', () => show( current ) );
	whenVisible( dock, async signal =>
	{
		restart();
		await new Promise( r => signal.addEventListener( 'abort', r ) );
		clearInterval( timer );
	} );
} )();

/* ------------------------------------------------------------------
 * 04 Background: a hidden chat asks; you answer from the banner.
 * ------------------------------------------------------------------ */
( () =>
{
	const row = $( '#bg-done' );
	const note = $( '#bg-note' );
	const toast = $( '#toast' );
	const reply = $( '#toast-reply' );
	const indicator = () => row.firstElementChild;
	whenVisible( toast.parentElement, async signal =>
	{
		for ( ;; )
		{
			indicator().className = 'spin';
			note.style.opacity = '.4';
			note.lastChild.textContent = 'No new activity';
			reply.textContent = '';
			toast.classList.remove( 'show' );
			await wait( 2000, signal );
			indicator().className = 'dot attn';
			note.style.opacity = '1';
			note.lastChild.textContent = 'Ask form fix needs you';
			toast.classList.add( 'show' );
			await wait( 1400, signal );
			await typeInto( reply, 'Yes, keep it behind a setting.', signal, 40 );
			await wait( 700, signal );
			toast.classList.remove( 'show' );
			indicator().className = 'spin';
			note.lastChild.textContent = 'Answered from the banner';
			await wait( 2600, signal );
			indicator().className = 'dot';
			note.lastChild.textContent = 'Ask form fix finished';
			await wait( 3000, signal );
		}
	} );
} )();

/* ------------------------------------------------------------------
 * 06 Agents: tool calls open panels in your layout.
 * ------------------------------------------------------------------ */
( () =>
{
	const dock = $( '#ag-dock' );
	const branch = $( '#ag-branch' );
	const steps = $$( '.step', dock );
	const states = [
		{ chat: [ 0, 0, 1, 1 ] },
		{ chat: [ 0, 0, 1, 1 ] },
		{ chat: [ 0, 0, 0.5, 1 ], term: [ 0.5, 0, 0.5, 1 ] },
		{ chat: [ 0, 0, 0.42, 1 ], web: [ 0.42, 0, 0.58, 0.62 ], term: [ 0.42, 0.62, 0.58, 0.38 ] },
		{ chat: [ 0, 0, 0.42, 1 ], web: [ 0.42, 0, 0.58, 0.5 ], report: [ 0.42, 0.5, 0.58, 0.5 ] },
		{ chat: [ 0, 0, 0.42, 1 ], web: [ 0.42, 0, 0.58, 0.5 ], report: [ 0.42, 0.5, 0.58, 0.5 ] },
	];
	let state = 0;
	const set = i => { state = i; Dock.apply( dock, states[ i ] ); };
	set( 0 );
	addEventListener( 'resize', () => set( state ) );
	whenVisible( dock, async signal =>
	{
		for ( ;; )
		{
			steps.forEach( s =>
			{
				s.classList.remove( 'show' );
				const st = $( '.st', s );
				if ( st )
				{
					st.className = 'st spin';
					st.innerHTML = '';
				}
			} );
			branch.textContent = 'main';
			set( 0 );
			await wait( 1000, signal );
			for ( const s of steps )
			{
				s.classList.add( 'show' );
				await wait( 900, signal );
				const n = Number( s.dataset.s );
				if ( n === 1 )
					branch.textContent = 'perf/pricing';
				if ( n < states.length )
					set( n );
				const st = $( '.st', s );
				if ( st )
				{
					st.className = 'st ok-ico';
					st.innerHTML = '<svg class="ico"><use href="#i-check"/></svg>';
				}
				await wait( 700, signal );
			}
			await wait( 5000, signal );
		}
	} );
} )();

/* ------------------------------------------------------------------
 * 07 Modules: type, save, compile, reload.
 * ------------------------------------------------------------------ */
( () =>
{
	const line = $( '#mod-line' );
	const dirty = $( '#mod-dirty' );
	const log = $( '#mod-log' );
	const panel = $( '#mod-panel' );
	const wrap = $( '#mod-panel-wrap' );
	const original = line.innerHTML;
	const typed = 'new Text { Content = $"{Words( _text.Value )} words" },';
	const highlight = s => s
		.replace( /\bnew\b/g, '<span class="k">new</span>' )
		.replace( /\bText\b/, '<span class="ty">Text</span>' )
		.replace( /\$"[^"]*"/, m => `<span class="s">${ m }</span>` )
		.replace( /\bWords\b/, '<span class="fn">Words</span>' );
	const say = html => { log.innerHTML += html + '\n'; };
	whenVisible( line, async signal =>
	{
		for ( ;; )
		{
			line.innerHTML = original;
			dirty.textContent = '';
			log.innerHTML = '';
			panel.classList.remove( 'v2' );
			say( 'watching notes · 2 files' );
			say( '<span class="ok">LOADED</span>   notes 0.1.0' );
			await wait( 1400, signal );
			const code = document.createElement( 'span' );
			line.replaceChildren( document.createTextNode( '\t\t\t' ), code );
			dirty.textContent = ' ●';
			let text = '';
			for ( const ch of typed )
			{
				text += ch;
				code.innerHTML = highlight( text );
				await wait( 26 + Math.random() * 30, signal );
			}
			await wait( 500, signal );
			dirty.textContent = '';
			await wait( 300, signal );
			say( 'compile  notes …' );
			await wait( 900, signal );
			say( 'compile  notes <span class="ok">ok</span> · 214 ms' );
			await wait( 300, signal );
			say( '<span class="ok">RELOADED</span> notes 0.1.1 · state kept' );
			panel.classList.add( 'v2' );
			wrap.classList.remove( 'flash' );
			void wrap.offsetWidth;
			wrap.classList.add( 'flash' );
			await wait( 5200, signal );
		}
	} );
} )();

/* ------------------------------------------------------------------
 * 08 Everywhere: wires from each client to the daemon.
 * ------------------------------------------------------------------ */
( () =>
{
	const net = $( '#net' );
	const host = $( '#n-host' );
	const phone = $( '#phone-type' );
	const box = el =>
	{
		const a = el.getBoundingClientRect(), b = net.getBoundingClientRect();
		return { x: a.left - b.left, y: a.top - b.top, w: a.width, h: a.height };
	};
	const route = () =>
	{
		if ( !net.offsetParent )
			return;
		const h = box( host );
		const hy = h.y + h.h / 2;
		const L = box( $( '#n-left' ) ), M = box( $( '#n-mid' ) ), R = box( $( '#n-right' ) );
		const lx = L.x + L.w / 2, rx = R.x + R.w / 2;
		const ly = L.y + L.h, ry = R.y + R.h;
		$( '#w-left' ).setAttribute( 'd', `M${ lx } ${ ly + 6 } V${ hy - 16 } Q${ lx } ${ hy } ${ lx + 16 } ${ hy } H${ h.x }` );
		$( '#w-mid' ).setAttribute( 'd', `M${ M.x + M.w / 2 } ${ M.y + M.h + 6 } V${ h.y }` );
		$( '#w-right' ).setAttribute( 'd', `M${ h.x + h.w } ${ hy } H${ rx - 16 } Q${ rx } ${ hy } ${ rx } ${ hy - 16 } V${ ry + 6 }` );
		const ll = $( '#l-left' ), lr = $( '#l-right' );
		ll.style.left = `${ ( lx + h.x ) / 2 }px`;
		ll.style.top = `${ hy }px`;
		lr.style.left = `${ ( h.x + h.w + rx ) / 2 }px`;
		lr.style.top = `${ hy }px`;
	};
	addEventListener( 'resize', route );
	new IntersectionObserver( route ).observe( net );
	document.fonts.ready.then( route );
	whenVisible( net, async signal =>
	{
		for ( ;; )
		{
			await typeInto( phone, 'merge when green', signal, 70 );
			await wait( 2200, signal );
			phone.textContent = '';
			await wait( 900, signal );
		}
	} );
} )();

/* ------------------------------------------------------------------
 * 09 Themes: every window on the page follows the picked theme.
 * Colors from Core/Theme.cs and Tern's catalog (Shell/Theming/TernThemes.json).
 * ------------------------------------------------------------------ */
( () =>
{
	const keys = [ 'canvas', 'panel', 'raised', 'hover', 'text', 'muted', 'border', 'accent', 'attn', 'ok', 'err', 'div', 'yellow', 'magenta', 'cyan', 'chrome' ];
	const themes = [
		[ 'Loom', '#262624 #222220 #2d2d29 #3a3a34 #e8e6df #a09f96 #3b3b35 #d5a583 #8fb4d9 #9bcb9a #e6a09a #353531 #dfc07f #c59bd0 #80c2bd #242425' ],
		[ 'dark-catppuccin', '#1e1e2e #181825 #313244 #45475a #cdd6f4 #a6adc8 #45475a #cba6f7 #89b4fa #a6e3a1 #f38ba8 #313244 #f9e2af #f5c2e7 #94e2d5 #11111b' ],
		[ 'dark-nord', '#2e3440 #2b303b #3b4252 #434c5e #eceff4 #a3acbd #434c5e #88c0d0 #81a1c1 #a3be8c #bf616a #3b4252 #ebcb8b #b48ead #8fbcbb #272c36' ],
		[ 'dark-gruvbox', '#282828 #232323 #32302f #3c3836 #ebdbb2 #a89984 #3c3836 #fe8019 #83a598 #b8bb26 #fb4934 #32302f #fabd2f #d3869b #8ec07c #1d2021' ],
		[ 'dark-dracula', '#282a36 #21222c #343746 #44475a #f8f8f2 #a2a6c4 #44475a #bd93f9 #8be9fd #50fa7b #ff5555 #343746 #f1fa8c #ff79c6 #8be9fd #1e1f29' ],
		[ 'alabaster', '#fdfcfb #f6f5f3 #efeeec #e8e8ec #1a1c20 #6a7080 #e1e1e6 #505860 #404860 #405840 #704040 #e8e8ec #806850 #6a5070 #40606a #f3f2f0' ],
	];
	const list = $( '#themes-list' );
	const root = document.documentElement.style;
	for ( const [ i, [ name, spec ] ] of themes.entries() )
	{
		const c = spec.split( ' ' );
		const b = document.createElement( 'button' );
		b.className = 'theme' + ( i === 0 ? ' on' : '' );
		b.innerHTML = `<span class="sw">${ [ 0, 2, 7, 8, 9, 10 ].map( k => `<i style="background:${ c[ k ] }"></i>` ).join( '' ) }</span>${ name }`;
		b.addEventListener( 'click', () =>
		{
			keys.forEach( ( k, n ) => root.setProperty( `--c-${ k }`, c[ n ] ) );
			$$( '.theme', list ).forEach( t => t.classList.toggle( 'on', t === b ) );
		} );
		list.append( b );
	}
} )();
