// Loom report kit script: adds the chart pattern definitions loom-report.css refers to, and the ⚙ "Customize report" panel in the bottom-right
// corner, which sets the report's paper, chart style and type and remembers them for this report.
( () => {
	if ( window.loomReportKit )
		return;
	window.loomReportKit = true;
	const papers = [
		["paper", "#f8f7f4"], ["cream", "#fbf4d8"], ["pink", "#f9e2e6"], ["sky", "#dde9f3"], ["mint", "#dfeedd"],
		["lavender", "#efe1f2"], ["peach", "#fde6d8"], ["gray", "#e8e9eb"], ["ink", "#1f1d1a"],
	];
	const charts = [["hatch", "Hatched"], ["flat", "Flat"], ["sketch", "Sketched"]];
	const types = [["serif", "Exposure", "Aa", "font-family: 'Loom Serif', Georgia, serif; font-style: italic; font-weight: 600; font-variation-settings: 'SOFT' 100, 'WONK' 0"],
		["sans", "Geist", "Aa", "font-family: 'Loom Grotesk', 'Segoe UI', sans-serif; font-weight: 500"],
		["bold", "Inter", "Aa", "font-family: 'Loom Sans', 'Segoe UI', sans-serif; font-weight: 800"]];
	const body = document.body;
	const key = "loom-report:" + location.pathname;

	// Patterns for svg .fill-2, .fill-3, .fill-5 and .area, drawn in the page's ink on its paper, and the rough-edge filter of the sketch style.
	const ns = "http://www.w3.org/2000/svg";
	const defs = document.createElementNS( ns, "svg" );
	defs.setAttribute( "width", "0" );
	defs.setAttribute( "height", "0" );
	defs.setAttribute( "aria-hidden", "true" );
	defs.style.position = "absolute";
	defs.innerHTML = `<defs>
		<pattern id="loom-hatch" width="4.5" height="4.5" patternUnits="userSpaceOnUse" patternTransform="rotate(45)"><rect width="4.5" height="4.5" style="fill: color-mix(in srgb, var(--ink) 30%, var(--paper))"/><line x1="0" y1="0" x2="0" y2="4.5" style="stroke: var(--ink)" stroke-width="1.5"/></pattern>
		<pattern id="loom-hatch-bold" width="6" height="6" patternUnits="userSpaceOnUse" patternTransform="rotate(45)"><rect width="6" height="6" style="fill: var(--paper)"/><line x1="0" y1="0" x2="0" y2="6" style="stroke: var(--ink)" stroke-width="2.6"/></pattern>
		<pattern id="loom-grid" width="4" height="4" patternUnits="userSpaceOnUse"><rect width="4" height="4" style="fill: color-mix(in srgb, var(--ink) 30%, var(--paper))"/><path d="M4 0H0V4" fill="none" style="stroke: var(--ink)" stroke-width="1.2"/></pattern>
		<pattern id="loom-dots" width="4" height="4" patternUnits="userSpaceOnUse"><rect width="4" height="4" style="fill: var(--paper)"/><circle cx="2" cy="2" r="0.75" style="fill: var(--ink)"/></pattern>
	</defs>`;
	body.prepend( defs );

	// A report without a choice of its own keeps what it was written with; saved choices win.
	const saved = JSON.parse( localStorage.getItem( key ) || "{}" );
	for ( const [name, value] of Object.entries( saved ) )
		body.dataset[name] = value;
	const save = () => localStorage.setItem( key, JSON.stringify( { paper: body.dataset.paper, chart: body.dataset.chart, type: body.dataset.type } ) );

	// The sketch style draws the whole report by hand. Chart shapes are redrawn as paths whose outlines move sideways by a smooth, slow wave, so
	// edges stay sharp at any zoom; every border and rule of the page (boxes, tables, tags, rules, the timeline, legend dots) is hidden and drawn
	// again as a pen line on one overlay. The originals come back when the style changes.
	const sketched = [];
	let overlay = null;
	const sketch = () => {
		for ( const [original, copy] of sketched.splice( 0 ) )
		{
			copy.remove();
			original.style.display = "";
		}
		sketchBorders();
		if ( body.dataset.chart !== "sketch" )
			return;
		let seed = 1;
		for ( const shape of document.querySelectorAll( ".report svg :is([class^='fill-'], .line, .area, .outlined, .shape, .stroke, .gridline)" ) )
		{
			if ( !( shape instanceof SVGGeometryElement ) || /m/.test( ( shape.getAttribute( "d" ) || "" ).slice( 1 ) ) )
				continue;
			const pixels = shape.getScreenCTM()?.a || 1;
			const subpaths = shape.tagName === "path" ? shape.getAttribute( "d" ).split( /(?=M)/ ) : [null];
			let d = "";
			for ( const sub of subpaths )
			{
				const piece = sub === null ? shape : document.createElementNS( ns, "path" );
				if ( sub !== null )
					piece.setAttribute( "d", sub );
				const length = piece.getTotalLength();
				const closed = sub === null ? !["line", "polyline"].includes( shape.tagName ) : /z\s*$/i.test( sub );
				const amplitude = 1.1 / pixels;
				const phase = [seed * 1.7, seed * 4.1];
				seed++;
				const at = s => piece.getPointAtLength( Math.min( Math.max( s, 0 ), length ) );
				const direction = ( s, ds ) => { const a = at( s - ds ), b = at( s + ds ); return Math.atan2( b.y - a.y, b.x - a.x ); };
				const step = 1 / pixels;
				// Corners stay where they are and stay sharp, so neighbouring slices still meet: they are where each path command ends.
				const corners = [0];
				if ( sub !== null )
				{
					const commands = sub.match( /[a-yA-Y][^a-zA-Z]*/g ) || [];
					const prefix = document.createElementNS( ns, "path" );
					for ( let k = 1; k < commands.length; k++ )
					{
						prefix.setAttribute( "d", commands.slice( 0, k + 1 ).join( "" ) );
						corners.push( prefix.getTotalLength() );
					}
				}
				else if ( shape.tagName === "rect" )
				{
					const w = shape.width.baseVal.value, h = shape.height.baseVal.value;
					corners.push( w, w + h, 2 * w + h );
				}
				corners.push( length );
				for ( let c = corners.length - 1; c > 0; c-- )
					if ( corners[c] - corners[c - 1] < 0.5 * step )
						corners.splice( c, 1 );
				const fmt = p => `${p[0].toFixed( 2 )} ${p[1].toFixed( 2 )}`;
				const start = at( 0 );
				d += `M${fmt( [start.x, start.y] )}`;
				for ( let c = 0; c < corners.length - 1; c++ )
				{
					// Between two corners: points move sideways by two slow waves that fade to nothing at both corners.
					const from = corners[c], span = corners[c + 1] - from;
					const count = Math.max( 2, Math.round( span * pixels / 7 ) );
					const points = [];
					for ( let i = 0; i <= count; i++ )
					{
						const s = from + span * i / count;
						const p = at( s );
						const angle = direction( s, 0.5 * step );
						const t = s * pixels;
						const fade = Math.max( 0, Math.sin( Math.PI * i / count ) ) ** 0.5;
						const offset = ( Math.sin( t / 23 + phase[0] ) * 0.65 + Math.sin( t / 9 + phase[1] ) * 0.35 ) * fade;
						points.push( [p.x - Math.sin( angle ) * offset * amplitude, p.y + Math.cos( angle ) * offset * amplitude] );
					}
					// Catmull-Rom through the points, as cubic curves.
					for ( let i = 0; i < points.length - 1; i++ )
					{
						const p0 = points[Math.max( i - 1, 0 )], p1 = points[i], p2 = points[i + 1], p3 = points[Math.min( i + 2, points.length - 1 )];
						const c1 = [p1[0] + ( p2[0] - p0[0] ) / 6, p1[1] + ( p2[1] - p0[1] ) / 6];
						const c2 = [p2[0] - ( p3[0] - p1[0] ) / 6, p2[1] - ( p3[1] - p1[1] ) / 6];
						d += `C${fmt( c1 )} ${fmt( c2 )} ${fmt( p2 )}`;
					}
				}
				if ( closed )
					d += "Z";
			}
			const copy = document.createElementNS( ns, "path" );
			copy.setAttribute( "class", shape.getAttribute( "class" ) );
			copy.setAttribute( "d", d );
			if ( shape.getAttribute( "transform" ) )
				copy.setAttribute( "transform", shape.getAttribute( "transform" ) );
			shape.after( copy );
			shape.style.display = "none";
			sketched.push( [shape, copy] );
		}
	};

	// Seeded random numbers, so a report looks the same each time it opens.
	let state = 7;
	const random = () => ( ( state = ( state * 16807 ) % 2147483647 ) / 2147483647 );
	// A smooth curve through points, as cubic Bézier segments (Catmull-Rom).
	const curve = points => {
		const fmt = p => `${p[0].toFixed( 1 )} ${p[1].toFixed( 1 )}`;
		let d = `M${fmt( points[0] )}`;
		for ( let i = 0; i < points.length - 1; i++ )
		{
			const p0 = points[Math.max( i - 1, 0 )], p1 = points[i], p2 = points[i + 1], p3 = points[Math.min( i + 2, points.length - 1 )];
			d += `C${fmt( [p1[0] + ( p2[0] - p0[0] ) / 6, p1[1] + ( p2[1] - p0[1] ) / 6] )} ${fmt( [p2[0] - ( p3[0] - p1[0] ) / 6, p2[1] - ( p3[1] - p1[1] ) / 6] )} ${fmt( p2 )}`;
		}
		return d;
	};
	// A pen line from one point to another: it overshoots its ends a little and drifts sideways by a slow wave.
	const penLine = ( x1, y1, x2, y2 ) => {
		const length = Math.hypot( x2 - x1, y2 - y1 ) || 1, ux = ( x2 - x1 ) / length, uy = ( y2 - y1 ) / length;
		const start = -0.5 - random() * 1.5, end = length + 0.5 + random() * 1.5;
		const count = Math.max( 2, Math.round( length / 16 ) ), phase = random() * 6.28, bow = ( random() - 0.5 ) * Math.min( 2.2, length / 60 );
		const points = [];
		for ( let i = 0; i <= count; i++ )
		{
			const s = start + ( end - start ) * i / count, u = i / count;
			const offset = Math.sin( s / 19 + phase ) * 0.55 + Math.sin( Math.PI * u ) * bow;
			points.push( [x1 + ux * s - uy * offset, y1 + uy * s + ux * offset] );
		}
		return curve( points );
	};
	// A pen circle around a box: one loop that does not quite close, and overlaps its start a little.
	const penCircle = ( cx, cy, rx, ry ) => {
		const begin = random() * 6.28, sweep = 6.28 + 0.25 + random() * 0.2, count = Math.max( 10, Math.round( ( rx + ry ) * 1.2 ) );
		const points = [];
		for ( let i = 0; i <= count; i++ )
		{
			const a = begin + sweep * i / count, wobble = 1 + Math.sin( a * 2 + begin ) * 0.04 + ( i / count ) * 0.05;
			points.push( [cx + Math.cos( a ) * rx * wobble, cy + Math.sin( a ) * ry * wobble] );
		}
		return curve( points );
	};
	const sketchBorders = () => {
		overlay?.remove();
		overlay = null;
		for ( const element of document.querySelectorAll( ".loom-sketched" ) )
			element.classList.remove( "loom-sketched" );
		if ( body.dataset.chart !== "sketch" )
			return;
		state = 7;
		const lines = [];
		const pen = ( d, color, width ) => lines.push( `<path d="${d}" stroke="${color}" stroke-width="${Math.max( width, 1 ) * 1.15}"/>` );
		const scrollX = window.scrollX, scrollY = window.scrollY;
		for ( const element of document.querySelectorAll( ".report *:not(svg *)" ) )
		{
			const css = getComputedStyle( element );
			const box = element.getBoundingClientRect();
			// Invisible parts, such as a citation's source card until it is hovered, keep their own straight border.
			if ( !box.width || !box.height || ( () => { for ( let e = element; e && e !== body; e = e.parentElement ) if ( getComputedStyle( e ).opacity === "0" ) return true; return false; } )() )
				continue;
			const left = box.left + scrollX, top = box.top + scrollY, right = left + box.width, bottom = top + box.height;
			// A table row is ruled once across its width, not once per cell.
			if ( element.tagName === "TD" || element.tagName === "TH" )
			{
				if ( element === element.parentElement.firstElementChild && parseFloat( css.borderBottomWidth ) > 0 && css.borderBottomStyle !== "none" )
				{
					const row = element.parentElement.getBoundingClientRect();
					pen( penLine( row.left + scrollX, bottom - 0.5, row.right + scrollX, bottom - 0.5 ), css.borderBottomColor, parseFloat( css.borderBottomWidth ) );
				}
				element.classList.add( "loom-sketched" );
				continue;
			}
			if ( element.classList.contains( "timeline" ) )
			{
				const line = getComputedStyle( element, "::before" );
				pen( penLine( left, top + parseFloat( line.top ) + 0.5, right, top + parseFloat( line.top ) + 0.5 ), line.borderTopColor, 1 );
				element.classList.add( "loom-sketched" );
			}
			const sides = ["Top", "Right", "Bottom", "Left"].filter( side => parseFloat( css[`border${side}Width`] ) > 0 && css[`border${side}Style`] !== "none"
				&& css[`border${side}Color`] !== "rgba(0, 0, 0, 0)" );
			if ( !sides.length )
				continue;
			const width = parseFloat( css[`border${sides[0]}Width`] ), color = css[`border${sides[0]}Color`];
			if ( sides.length === 4 && parseFloat( css.borderTopLeftRadius ) >= Math.min( box.width, box.height ) / 2 - 1 )
				pen( penCircle( ( left + right ) / 2, ( top + bottom ) / 2, box.width / 2 - width / 2, box.height / 2 - width / 2 ), color, width );
			else
			{
				const h = width / 2;
				const edges = { Top: [left, top + h, right, top + h], Right: [right - h, top, right - h, bottom], Bottom: [right, bottom - h, left, bottom - h], Left: [left + h, bottom, left + h, top] };
				for ( const side of sides )
					pen( penLine( ...edges[side] ), css[`border${side}Color`], parseFloat( css[`border${side}Width`] ) );
			}
			element.classList.add( "loom-sketched" );
		}
		overlay = document.createElementNS( ns, "svg" );
		overlay.setAttribute( "class", "loom-sketch" );
		overlay.setAttribute( "aria-hidden", "true" );
		overlay.setAttribute( "width", "1" );
		overlay.setAttribute( "height", "1" );
		overlay.innerHTML = `<g fill="none" stroke-linecap="round">${lines.join( "" )}</g>`;
		body.append( overlay );
	};
	let pending = 0;
	const resketch = () => {
		cancelAnimationFrame( pending );
		pending = requestAnimationFrame( sketchBorders );
	};
	addEventListener( "resize", resketch );
	document.fonts.ready.then( resketch );
	sketch();

	const style = document.createElement( "style" );
	style.textContent = `
		.loom-sketched { border-color: transparent !important; }
		.loom-sketched.timeline::before { border-color: transparent; }
		.loom-sketch { position: absolute; left: 0; top: 0; pointer-events: none; overflow: visible; }
		.loom-capturing .loom-cog, .loom-capturing .loom-panel { display: none; }
		.loom-cog, .loom-panel { position: fixed; z-index: 2147483000; font: 13px/1.4 "Segoe UI", system-ui, sans-serif; color: #1a1a1a; }
		.loom-cog { right: 20px; bottom: 20px; width: 40px; height: 40px; border-radius: 50%; border: 1px solid rgba(0,0,0,.08); background: #fff;
			box-shadow: 0 2px 10px rgba(0,0,0,.08); cursor: pointer; display: grid; place-items: center; font-size: 18px; opacity: .55; transition: opacity .2s; }
		.loom-cog:hover, .loom-cog[aria-expanded="true"] { opacity: 1; }
		.loom-panel { right: 20px; bottom: 72px; width: 300px; padding: 20px; border-radius: 16px; background: #fff; border: 1px solid rgba(0,0,0,.06);
			box-shadow: 0 12px 40px rgba(0,0,0,.14); }
		.loom-panel[hidden] { display: none; }
		.loom-panel h6 { margin: 0 0 12px; font-size: 11px; font-weight: 600; letter-spacing: .14em; text-transform: uppercase; color: #8a8a8a; }
		.loom-panel h6 + * { margin-bottom: 20px; }
		.loom-papers { display: flex; flex-wrap: wrap; gap: 8px; }
		.loom-papers button { width: 26px; height: 26px; border-radius: 50%; border: 1px solid rgba(0,0,0,.1); cursor: pointer; padding: 0; }
		.loom-papers button[aria-pressed="true"] { outline: 2px solid #1a1a1a; outline-offset: 2px; }
		.loom-charts { display: flex; gap: 8px; }
		.loom-charts button { width: 52px; height: 52px; border-radius: 10px; border: 1px solid rgba(0,0,0,.1); background: #fff; cursor: pointer; display: grid; place-items: center; }
		.loom-charts button[aria-pressed="true"] { border: 2px solid #9a9a9a; }
		.loom-charts i { width: 24px; height: 24px; border: 1.5px solid #1a1a1a; }
		.loom-types { display: grid; gap: 2px; margin-bottom: 0 !important; }
		.loom-types button { display: flex; align-items: baseline; gap: 18px; padding: 6px 10px; border: 0; border-radius: 8px; background: none; cursor: pointer; text-align: left; color: #8a8a8a; font-size: 14px; }
		.loom-types button span { font-size: 24px; color: #1a1a1a; width: 34px; }
		.loom-types button[aria-pressed="true"] { background: #efefef; }
		.loom-comment, .loom-tray { position: absolute; z-index: 2147482000; font: 14px/1.45 "Segoe UI", system-ui, sans-serif; color: #1a1a1a; }
		.loom-comment { width: 340px; display: flex; align-items: flex-end; gap: 10px; padding: 12px 12px 12px 16px; background: #fff; border-radius: 14px;
			box-shadow: 0 10px 34px rgba(0,0,0,.16), 0 0 0 1px rgba(0,0,0,.05); }
		.loom-comment[hidden], .loom-tray[hidden] { display: none; }
		.loom-comment textarea { flex: 1; min-height: 22px; max-height: 160px; border: 0; outline: 0; resize: none; padding: 4px 0; font: inherit; color: inherit; background: none; }
		.loom-comment button, .loom-tray button { flex: none; border: 0; cursor: pointer; display: grid; place-items: center; font: inherit; }
		.loom-comment .send { width: 32px; height: 32px; border-radius: 50%; background: none; color: #555; font-size: 17px; }
		.loom-comment .send:hover { background: #f0f0f0; }
		.loom-comment .add { width: 34px; height: 34px; border-radius: 50%; background: #111; color: #fff; font-size: 15px; }
		.loom-tray { position: fixed; right: 72px; bottom: 22px; display: flex; align-items: center; gap: 10px; padding: 6px 6px 6px 14px; background: #fff;
			border-radius: 999px; box-shadow: 0 6px 22px rgba(0,0,0,.14), 0 0 0 1px rgba(0,0,0,.05); font-size: 13px; }
		.loom-tray button { height: 28px; padding: 0 14px; border-radius: 999px; background: #111; color: #fff; }
		.loom-tray button:disabled { opacity: .5; cursor: default; }
		.loom-tray .clear { background: none; color: #777; padding: 0 6px; }
		.loom-tray button[hidden] { display: none; }
		::highlight(loom-comment) { background-color: rgba(246, 214, 90, .55); }
		.loom-picked { outline: 2px solid rgba(232, 190, 50, .9); outline-offset: 6px; border-radius: 2px; }
		.report :is(svg, table, .stats, .timeline, .bars, .pullquote, blockquote, figure, img, .callout, .legend) { cursor: pointer; }
		@media print { .loom-comment, .loom-tray { display: none; } .loom-picked { outline: 0; } }
		.loom-capturing .loom-comment, .loom-capturing .loom-tray { display: none; }
		@media print { .loom-cog, .loom-panel { display: none; } }`;
	document.head.append( style );

	const cog = document.createElement( "button" );
	cog.className = "loom-cog";
	cog.title = "Customize report";
	cog.setAttribute( "aria-label", "Customize report" );
	cog.setAttribute( "aria-expanded", "false" );
	cog.textContent = "⚙";

	const panel = document.createElement( "div" );
	panel.className = "loom-panel";
	panel.hidden = true;
	panel.setAttribute( "role", "dialog" );
	panel.setAttribute( "aria-label", "Customize report" );
	const chartSwatch = { hatch: "background: repeating-linear-gradient(135deg,#1a1a1a 0 1.5px,transparent 1.5px 5px)", flat: "background:#a8a8a8",
		sketch: "background: repeating-linear-gradient(135deg,#1a1a1a 0 2.6px,transparent 2.6px 6px); border-width: 2.4px; border-radius: 2px 4px 3px 5px" };
	panel.innerHTML = `
		<h6>Customize report</h6>
		<div class="loom-papers">${papers.map( ( [name, color] ) => `<button data-set="paper" data-value="${name}" title="${name}" style="background:${color}"></button>` ).join( "" )}</div>
		<h6>Style</h6>
		<div class="loom-charts">${charts.map( ( [name, label] ) => `<button data-set="chart" data-value="${name}" title="${label}"><i style="${chartSwatch[name]}"></i></button>` ).join( "" )}</div>
		<div class="loom-types">${types.map( ( [name, label, glyph, font] ) => `<button data-set="type" data-value="${name}"><span style="${font}">${glyph}</span>${label}</button>` ).join( "" )}</div>`;

	const defaults = { paper: "peach", chart: "hatch", type: "serif" };
	const refresh = () => panel.querySelectorAll( "[data-set]" ).forEach( button =>
		button.setAttribute( "aria-pressed", String( ( body.dataset[button.dataset.set] || defaults[button.dataset.set] ) === button.dataset.value ) ) );
	panel.addEventListener( "click", event => {
		const button = event.target.closest( "[data-set]" );
		if ( !button )
			return;
		body.dataset[button.dataset.set] = button.dataset.value;
		if ( button.dataset.set === "chart" )
			sketch();
		save();
		refresh();
	} );
	cog.addEventListener( "click", () => {
		panel.hidden = !panel.hidden;
		cog.setAttribute( "aria-expanded", String( !panel.hidden ) );
		refresh();
	} );
	document.addEventListener( "keydown", event => {
		if ( event.key === "Escape" && !panel.hidden )
			cog.click();
	} );
	document.addEventListener( "pointerdown", event => {
		if ( !panel.hidden && !panel.contains( event.target ) && event.target !== cog )
			cog.click();
	} );

	// Comments, inside Loom only: select text, or click a chart, table or other figure, and a card opens under it. Its arrow sends the comment, with any
	// queued before it, to the chat that made the report, which revises it; its check queues the comment, so several go together. Queued text stays
	// marked in yellow and queued figures outlined until they are sent.
	if ( window.chrome?.webview && window.CSS?.highlights )
	{
		const figures = ".report :is(svg, table, .stats, .timeline, .bars, .pullquote, blockquote, figure, img, .callout, .legend)";
		const marks = new Highlight();
		CSS.highlights.set( "loom-comment", marks );
		const queue = [];
		let target = null;

		const card = document.createElement( "div" );
		card.className = "loom-comment";
		card.hidden = true;
		card.innerHTML = `<textarea rows="1" placeholder="Comment" aria-label="Comment"></textarea>
			<button class="send" title="Send now (Enter)" aria-label="Send now">\u27A4</button><button class="add" title="Add to the queue (Ctrl+Enter)" aria-label="Add to the queue">\u2713</button>`;
		const field = card.querySelector( "textarea" );
		const tray = document.createElement( "div" );
		tray.className = "loom-tray";
		tray.hidden = true;
		tray.innerHTML = `<span></span><button class="clear" title="Remove the queued comments">Clear</button><button class="go">Send</button>`;
		const [trayText, clearButton, sendButton] = tray.children;
		let trayTimer = 0;

		const clip = ( text, length ) => ( text = text.replace( /\s+/g, " " ).trim() ).length > length ? text.slice( 0, length - 1 ) + "\u2026" : text;
		// Names a figure by its own caption, the note beside it, or its kind and first words.
		const describe = element => {
			const caption = element.closest( "figure" )?.querySelector( "figcaption" ) || element.parentElement?.querySelector( ":scope > .fig" )
				|| element.closest( ".aside-grid" )?.querySelector( ".note-title" );
			const kind = element.matches( "svg, img" ) ? "figure" : element.matches( "table" ) ? "table" : element.matches( ".stats" ) ? "key numbers"
				: element.matches( ".timeline" ) ? "timeline" : element.matches( ".bars" ) ? "bar chart" : element.matches( "blockquote, .pullquote" ) ? "quote" : "block";
			return `the ${kind} ${clip( caption?.textContent || element.textContent || "", 120 )}`.trim();
		};
		const place = rect => {
			card.style.left = `${Math.max( 12, Math.min( rect.left + scrollX, document.documentElement.clientWidth - 352 + scrollX ) )}px`;
			card.style.top = `${rect.bottom + scrollY + 10}px`;
		};
		const close = () => {
			if ( target?.element && !queue.some( item => item.element === target.element ) )
				target.element.classList.remove( "loom-picked" );
			target = null;
			card.hidden = true;
		};
		const open = ( next, rect ) => {
			close();
			target = next;
			target.element?.classList.add( "loom-picked" );
			place( rect );
			card.hidden = false;
			field.value = "";
			field.style.height = "";
			field.focus( { preventScroll: true } );
		};
		const showTray = ( text, isFinal ) => {
			clearTimeout( trayTimer );
			trayText.textContent = text ?? `${queue.length} comment${queue.length === 1 ? "" : "s"}`;
			clearButton.hidden = sendButton.hidden = isFinal || queue.length === 0;
			sendButton.disabled = false;
			tray.hidden = !text && queue.length === 0;
			if ( isFinal )
				trayTimer = setTimeout( () => showTray(), 3500 );
		};
		const add = () => {
			const comment = field.value.trim();
			if ( !target || !comment )
				return false;
			queue.push( { ...target, comment } );
			if ( target.range )
				marks.add( target.range );
			target = null;
			card.hidden = true;
			getSelection().removeAllRanges();
			showTray();
			return true;
		};
		const clearQueue = () => {
			for ( const item of queue.splice( 0 ) )
				item.element?.classList.remove( "loom-picked" );
			marks.clear();
		};
		const send = () => {
			if ( !queue.length )
				return;
			sendButton.disabled = true;
			trayText.textContent = "Sending\u2026";
			window.chrome.webview.postMessage( { loomComments: queue.map( ( { about, comment } ) => ( { about, comment } ) ) } );
		};
		window.chrome.webview.addEventListener( "message", event => {
			if ( typeof event.data?.loomCommentsSent !== "boolean" )
				return;
			if ( event.data.loomCommentsSent )
			{
				clearQueue();
				showTray( "Sent to the chat \u2713", true );
			}
			else
				showTray( event.data.error || "Could not send the comments", true );
		} );

		card.querySelector( ".add" ).addEventListener( "click", add );
		card.querySelector( ".send" ).addEventListener( "click", () => ( add() || queue.length ) && send() );
		sendButton.addEventListener( "click", send );
		clearButton.addEventListener( "click", () => { clearQueue(); showTray(); } );
		field.addEventListener( "input", () => { field.style.height = "auto"; field.style.height = `${field.scrollHeight}px`; } );
		field.addEventListener( "keydown", event => {
			if ( event.key === "Escape" )
				close();
			else if ( event.key === "Enter" && !event.shiftKey )
			{
				event.preventDefault();
				if ( event.ctrlKey )
					add();
				else if ( add() || queue.length )
					send();
			}
		} );
		document.addEventListener( "mouseup", event => {
			if ( card.contains( event.target ) || tray.contains( event.target ) || panel.contains( event.target ) || event.target === cog )
				return;
			setTimeout( () => {
				const selection = getSelection();
				if ( selection.rangeCount && !selection.isCollapsed && selection.anchorNode && document.querySelector( ".report" )?.contains( selection.anchorNode ) )
				{
					const range = selection.getRangeAt( 0 ).cloneRange();
					open( { about: clip( selection.toString(), 300 ), range }, range.getBoundingClientRect() );
					return;
				}
				const figure = event.target.closest?.( figures );
				if ( figure && !event.target.closest( "a, .cite" ) )
					open( { about: describe( figure ), element: figure }, figure.getBoundingClientRect() );
				else if ( !field.value.trim() )
					close();
			} );
		} );
		body.append( card, tray );
	}
	body.append( panel, cog );
} )();
