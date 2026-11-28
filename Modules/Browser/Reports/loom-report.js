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
	defs.setAttribute( "data-loom-ui", "" );
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
		overlay.setAttribute( "data-loom-ui", "" );
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
	// The pen lines sit at fixed positions, so any change of layout redraws them: the window's size, another font or type style, fonts that finish
	// loading, or a revision patched in.
	addEventListener( "resize", resketch );
	new ResizeObserver( resketch ).observe( body );
	document.fonts.addEventListener( "loadingdone", resketch );
	document.fonts.ready.then( resketch );
	sketch();

	const style = document.createElement( "style" );
	style.textContent = `
		:root { --loom-panel: #222220; --loom-raised: #2d2d29; --loom-hover: #3a3a34; --loom-text: #e8e6df; --loom-muted: #a09f96; --loom-border: #3b3b35;
			--loom-accent: #d5a583; --loom-radius: 5px; --loom-font: "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif; }
		.loom-sketched { border-color: transparent !important; }
		.loom-sketched.timeline::before { border-color: transparent; }
		.loom-sketch { position: absolute; left: 0; top: 0; pointer-events: none; overflow: visible; }

		/* Loom's own controls on the page wear Loom's theme, which Loom sends the page, not the report's paper. */
		.loom-cog, .loom-panel, .loom-comment, .loom-tray, .loom-chip { font: 13px/1.45 var(--loom-font); color: var(--loom-text); color-scheme: dark; }
		.loom-cog, .loom-panel, .loom-comment, .loom-tray { background: var(--loom-panel); border: 1px solid var(--loom-border); box-shadow: 0 12px 32px rgba(0,0,0,.28); }
		.loom-cog, .loom-panel, .loom-tray { position: fixed; z-index: 2147483000; }
		.loom-cog { right: 16px; bottom: 16px; width: 32px; height: 32px; border-radius: calc(var(--loom-radius) * 2); cursor: pointer; display: grid; place-items: center;
			font-size: 15px; color: var(--loom-muted); opacity: .7; transition: opacity .15s, color .15s; }
		.loom-cog:hover, .loom-cog[aria-expanded="true"] { opacity: 1; color: var(--loom-text); }
		.loom-panel { right: 16px; bottom: 56px; width: 280px; padding: 14px; border-radius: calc(var(--loom-radius) * 2); }
		.loom-panel[hidden] { display: none; }
		.loom-panel h6 { margin: 0 0 10px; font-size: 11px; font-weight: 600; color: var(--loom-muted); }
		.loom-panel h6 + * { margin-bottom: 16px; }
		.loom-papers { display: flex; flex-wrap: wrap; gap: 7px; }
		.loom-papers button { width: 22px; height: 22px; border-radius: 50%; border: 1px solid rgba(0,0,0,.25); cursor: pointer; padding: 0; }
		.loom-papers button[aria-pressed="true"] { outline: 2px solid var(--loom-accent); outline-offset: 2px; }
		.loom-charts { display: flex; gap: 6px; }
		.loom-charts button { width: 44px; height: 44px; border-radius: var(--loom-radius); border: 1px solid var(--loom-border); background: var(--loom-raised); color: var(--loom-text);
			cursor: pointer; display: grid; place-items: center; }
		.loom-charts button:hover { background: var(--loom-hover); }
		.loom-charts button[aria-pressed="true"] { border-color: var(--loom-accent); }
		.loom-charts i { width: 20px; height: 20px; border: 1.5px solid currentColor; }
		.loom-types { display: grid; gap: 2px; margin-bottom: 0 !important; }
		.loom-types button { display: flex; align-items: baseline; gap: 14px; padding: 4px 8px; border: 0; border-radius: var(--loom-radius); background: none; cursor: pointer;
			text-align: left; color: var(--loom-muted); font: inherit; }
		.loom-types button:hover { background: var(--loom-hover); }
		.loom-types button span { font-size: 20px; color: var(--loom-text); width: 30px; }
		.loom-types button[aria-pressed="true"] { background: var(--loom-raised); color: var(--loom-text); }

		.loom-comment { position: absolute; z-index: 2147482000; width: 360px; border-radius: calc(var(--loom-radius) * 2); overflow: hidden;
			animation: loom-in .12s ease-out; }
		@keyframes loom-in { from { opacity: 0; transform: translateY(-4px); } }
		.loom-comment[hidden], .loom-tray[hidden], .loom-chip[hidden] { display: none; }
		.loom-comment .about { margin: 12px 12px 0; padding-left: 9px; border-left: 2px solid var(--loom-accent); color: var(--loom-muted); font-size: 12px;
			display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; overflow: hidden; }
		.loom-comment textarea { display: block; width: 100%; box-sizing: border-box; min-height: 52px; max-height: 180px; margin: 0; padding: 10px 12px; border: 0;
			outline: 0; resize: none; font: inherit; font-size: 13.5px; color: var(--loom-text); background: none; }
		.loom-comment textarea::placeholder { color: var(--loom-muted); }
		.loom-comment footer { display: flex; align-items: center; gap: 6px; padding: 8px 8px 8px 12px; border-top: 1px solid var(--loom-border); background: var(--loom-raised); }
		.loom-comment footer small { flex: 1; color: var(--loom-muted); font-size: 11px; }
		.loom-comment kbd { font: inherit; padding: 0 4px; border: 1px solid var(--loom-border); border-radius: 3px; }
		.loom-comment button, .loom-tray button { height: 26px; padding: 0 11px; border-radius: var(--loom-radius); border: 1px solid var(--loom-border); background: var(--loom-raised);
			color: var(--loom-text); font: inherit; cursor: pointer; }
		.loom-comment button:hover, .loom-tray button:hover { background: var(--loom-hover); }
		.loom-comment button.primary, .loom-tray button.primary { border-color: var(--loom-accent); background: var(--loom-accent); color: var(--loom-panel); font-weight: 600; }
		.loom-comment button.primary:hover, .loom-tray button.primary:hover { filter: brightness(1.08); }
		.loom-comment button:disabled, .loom-tray button:disabled { opacity: .45; cursor: default; filter: none; }
		.loom-tray { right: 56px; bottom: 16px; display: flex; align-items: center; gap: 8px; padding: 4px 4px 4px 12px; border-radius: calc(var(--loom-radius) * 2); }
		.loom-tray button[hidden] { display: none; }
		.loom-tray .count { color: var(--loom-text); }
		.loom-chip { position: absolute; z-index: 2147481000; padding: 2px 8px; border-radius: var(--loom-radius); background: var(--loom-accent); color: var(--loom-panel);
			font-size: 11px; font-weight: 600; pointer-events: none; transform: translateY(-100%); }

		/* Feedback: a selection, the text a comment is being written on, queued comments, figures under the pointer, and what a revision changed. */
		.report ::selection { background: color-mix(in srgb, var(--loom-accent) 45%, transparent); }
		::highlight(loom-pending) { background-color: color-mix(in srgb, var(--loom-accent) 45%, transparent); }
		::highlight(loom-comment) { background-color: color-mix(in srgb, var(--loom-accent) 28%, transparent); text-decoration: underline 2px var(--loom-accent); text-underline-offset: 3px; }
		.loom-hovered { outline: 1.5px dashed var(--loom-accent); outline-offset: 6px; cursor: pointer; }
		.loom-picked { outline: 2px solid var(--loom-accent); outline-offset: 6px; }
		.loom-changed { animation: loom-changed 2.4s ease-out; border-radius: 3px; }
		@keyframes loom-changed { 0%, 35% { background-color: color-mix(in srgb, var(--loom-accent) 30%, transparent);
			box-shadow: 0 0 0 5px color-mix(in srgb, var(--loom-accent) 30%, transparent); } 100% { background-color: transparent; box-shadow: 0 0 0 5px transparent; } }
		.loom-capturing :is(.loom-cog, .loom-panel, .loom-comment, .loom-tray, .loom-chip) { display: none; }
		.loom-capturing :is(.loom-hovered, .loom-picked) { outline: 0; }
		@media print { :is(.loom-cog, .loom-panel, .loom-comment, .loom-tray, .loom-chip) { display: none; } :is(.loom-hovered, .loom-picked) { outline: 0; } }`;
	document.head.append( style );

	const cog = document.createElement( "button" );
	cog.className = "loom-cog";
	cog.title = "Customize report";
	cog.setAttribute( "aria-label", "Customize report" );
	cog.setAttribute( "aria-expanded", "false" );
	cog.textContent = "⚙";
	cog.setAttribute( "data-loom-ui", "" );

	const panel = document.createElement( "div" );
	panel.className = "loom-panel";
	panel.hidden = true;
	panel.setAttribute( "role", "dialog" );
	panel.setAttribute( "aria-label", "Customize report" );
	panel.setAttribute( "data-loom-ui", "" );
	const chartSwatch = { hatch: "background: repeating-linear-gradient(135deg,currentColor 0 1.5px,transparent 1.5px 5px)", flat: "background: currentColor; opacity: .55",
		sketch: "background: repeating-linear-gradient(135deg,currentColor 0 2.6px,transparent 2.6px 6px); border-width: 2.4px; border-radius: 2px 4px 3px 5px" };
	panel.innerHTML = `
		<h6>Paper</h6>
		<div class="loom-papers">${papers.map( ( [name, color] ) => `<button data-set="paper" data-value="${name}" title="${name}" style="background:${color}"></button>` ).join( "" )}</div>
		<h6>Charts and type</h6>
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
		else if ( button.dataset.set === "type" )
			resketch();
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

	// Inside Loom, the page talks to it: Loom sends its theme, which Loom's controls here wear, and a revised report, which replaces this one in place.
	const webview = window.chrome?.webview;
	webview?.addEventListener( "message", event => {
		const theme = event.data?.loomTheme;
		if ( theme )
			for ( const [name, value] of Object.entries( theme ) )
				document.documentElement.style.setProperty( `--loom-${name}`, value );
		if ( typeof event.data?.loomReplace === "string" )
		{
			replace( event.data.loomReplace );
			webview.postMessage( { loomReplaced: true } );
		}
	} );

	// Updates the page to a revised report without loading it again: each element, attribute and text that differs is patched, Loom's own controls
	// stay, the user's paper, chart style and type stay, and what changed glows for a moment, scrolled into view when the reader cannot see it.
	const isKit = node => node.nodeType === 1 && node.hasAttribute( "data-loom-ui" );
	const replace = html => {
		const next = new DOMParser().parseFromString( html, "text/html" );
		const chart = body.dataset.chart;
		if ( chart === "sketch" )
		{
			delete body.dataset.chart;
			sketch();
		}
		const changed = new Set();
		const mark = node => {
			const element = node?.nodeType === 1 ? node : node?.parentElement;
			if ( element && element !== body && element.closest( ".report" ) )
				changed.add( element );
		};
		const patch = ( old, fresh ) => {
			if ( old !== body )
			{
				// Loom's own marker classes, such as a picked figure's, are not the report's: losing them is no change.
				const own = value => value.replace( /(^|\s)loom-\S+/g, "" ).trim();
				for ( const { name, value } of [...fresh.attributes] )
				{
					const before = old.getAttribute( name );
					if ( before === value )
						continue;
					old.setAttribute( name, value );
					if ( name !== "class" || own( before ?? "" ) !== own( value ) )
						mark( old );
				}
				for ( const { name } of [...old.attributes] )
					if ( !fresh.hasAttribute( name ) )
					{
						if ( name !== "class" || own( old.getAttribute( name ) ) )
							mark( old );
						old.removeAttribute( name );
					}
			}
			// Whitespace between tags is not content: matching skips it, so a revision written with other line breaks patches the same elements.
			const isBlank = node => node.nodeType === 3 && !node.nodeValue.trim();
			const olds = [...old.childNodes].filter( node => !isKit( node ) && !isBlank( node ) );
			const freshes = [...fresh.childNodes].filter( node => !isBlank( node ) );
			freshes.forEach( ( node, index ) => {
				const current = olds[index];
				if ( current && current.nodeType === node.nodeType && current.nodeName === node.nodeName )
				{
					if ( node.nodeType === 1 )
						patch( current, node );
					else if ( current.nodeValue !== node.nodeValue )
					{
						const isSpace = current.nodeValue.trim() === node.nodeValue.trim();
						current.nodeValue = node.nodeValue;
						if ( !isSpace )
							mark( current );
					}
					return;
				}
				const copy = document.importNode( node, true );
				if ( current )
					current.replaceWith( copy );
				else
					old.insertBefore( copy, old === body ? [...body.childNodes].find( child => isKit( child ) && child !== defs ) ?? null : null );
				mark( copy );
			} );
			for ( const extra of olds.slice( freshes.length ) )
			{
				mark( extra.parentElement );
				extra.remove();
			}
		};
		patch( body, next.body );
		document.title = next.title;
		// The reader's own choices win over the revision's, as when the page opened.
		const own = JSON.parse( localStorage.getItem( key ) || "{}" );
		for ( const name of ["paper", "type"] )
			if ( !own[name] && next.body.dataset[name] )
				body.dataset[name] = next.body.dataset[name];
		if ( chart )
			body.dataset.chart = chart;
		sketch();
		// The innermost changes glow, not every section around them.
		const glowing = [...changed].filter( element => ![...changed].some( other => other !== element && element.contains( other ) ) );
		for ( const element of glowing )
		{
			element.classList.remove( "loom-changed" );
			void element.offsetWidth;
			element.classList.add( "loom-changed" );
			element.addEventListener( "animationend", () => element.classList.remove( "loom-changed" ), { once: true } );
		}
		const first = glowing.find( element => element.getClientRects().length );
		if ( first )
		{
			const box = first.getBoundingClientRect();
			if ( box.bottom < 0 || box.top > innerHeight )
				first.scrollIntoView( { behavior: "smooth", block: "center" } );
		}
	};

	// Comments, inside Loom only. Pointing at a chart, table or other figure outlines it; selected text stays marked. Releasing the mouse on a
	// selection, or clicking a figure, opens a card under it showing what the comment is about. Enter sends the comment, with any queued before it,
	// to the chat that made the report, which revises it in place; Ctrl+Enter queues it, so several go together. Queued text stays underlined and
	// queued figures outlined until they are sent.
	if ( webview && window.CSS?.highlights )
	{
		const figures = ".report :is(svg, table, .stats, .timeline, .bars, .pullquote, blockquote, figure, img, .callout, .legend)";
		const marks = new Highlight(), pending = new Highlight();
		CSS.highlights.set( "loom-comment", marks );
		CSS.highlights.set( "loom-pending", pending );
		const queue = [];
		let target = null, hovered = null, isSelecting = false;

		const card = document.createElement( "div" );
		card.className = "loom-comment";
		card.hidden = true;
		card.setAttribute( "data-loom-ui", "" );
		card.setAttribute( "role", "dialog" );
		card.setAttribute( "aria-label", "Comment" );
		card.innerHTML = `<div class="about"></div><textarea rows="2" placeholder="What should change here?" aria-label="Comment"></textarea>
			<footer><small><kbd>Enter</kbd> send · <kbd>Ctrl+Enter</kbd> queue</small><button class="add">Queue</button><button class="primary send">Send</button></footer>`;
		const aboutText = card.querySelector( ".about" ), field = card.querySelector( "textarea" );
		const addButton = card.querySelector( ".add" ), cardSend = card.querySelector( ".send" );
		const tray = document.createElement( "div" );
		tray.className = "loom-tray";
		tray.hidden = true;
		tray.setAttribute( "data-loom-ui", "" );
		tray.innerHTML = `<span class="count"></span><button class="clear">Clear</button><button class="primary go">Send</button>`;
		const [trayText, clearButton, sendButton] = tray.children;
		const chip = document.createElement( "div" );
		chip.className = "loom-chip";
		chip.hidden = true;
		chip.setAttribute( "data-loom-ui", "" );
		chip.textContent = "Click to comment";
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
		const hover = element => {
			if ( hovered === element )
				return;
			hovered?.classList.remove( "loom-hovered" );
			hovered = element;
			chip.hidden = !element;
			if ( !element )
				return;
			element.classList.add( "loom-hovered" );
			const box = element.getBoundingClientRect();
			chip.style.left = `${box.left + scrollX - 6}px`;
			chip.style.top = `${box.top + scrollY - 10}px`;
		};
		const updateButtons = () => {
			const isEmpty = !field.value.trim();
			addButton.disabled = isEmpty;
			cardSend.disabled = isEmpty && !queue.length;
			cardSend.textContent = queue.length && !isEmpty ? `Send ${queue.length + 1}` : queue.length ? `Send ${queue.length}` : "Send";
		};
		const close = () => {
			if ( target?.element && !queue.some( item => item.element === target.element ) )
				target.element.classList.remove( "loom-picked" );
			pending.clear();
			target = null;
			card.hidden = true;
		};
		const open = ( next, rect ) => {
			close();
			hover( null );
			target = next;
			target.element?.classList.add( "loom-picked" );
			if ( target.range )
				pending.add( target.range );
			aboutText.textContent = target.range ? `\u201C${target.about}\u201D` : target.about;
			const left = Math.max( 12, Math.min( rect.left, document.documentElement.clientWidth - 372 ) );
			card.style.left = `${left + scrollX}px`;
			card.style.top = `${rect.bottom + scrollY + 10}px`;
			card.hidden = false;
			field.value = "";
			field.style.height = "";
			updateButtons();
			getSelection().removeAllRanges();
			field.focus( { preventScroll: true } );
			const box = card.getBoundingClientRect();
			if ( box.bottom > innerHeight )
				scrollBy( { top: box.bottom - innerHeight + 16, behavior: "smooth" } );
		};
		const showTray = ( text, isFinal ) => {
			clearTimeout( trayTimer );
			trayText.textContent = text ?? `${queue.length} comment${queue.length === 1 ? "" : "s"} queued`;
			clearButton.hidden = sendButton.hidden = isFinal || text !== undefined || queue.length === 0;
			sendButton.disabled = false;
			tray.hidden = text === undefined && queue.length === 0;
			if ( isFinal )
				trayTimer = setTimeout( () => showTray(), 4000 );
		};
		const add = () => {
			const comment = field.value.trim();
			if ( !target || !comment )
				return false;
			queue.push( { ...target, comment } );
			if ( target.range )
				marks.add( target.range );
			pending.clear();
			target = null;
			card.hidden = true;
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
			showTray( "Sending\u2026" );
			webview.postMessage( { loomComments: queue.map( ( { about, comment } ) => ( { about, comment } ) ) } );
		};
		webview.addEventListener( "message", event => {
			if ( typeof event.data?.loomReplace === "string" )
			{
				close();
				showTray( "Report revised \u2713", true );
			}
			if ( typeof event.data?.loomCommentsSent !== "boolean" )
				return;
			if ( event.data.loomCommentsSent )
			{
				clearQueue();
				showTray( "Sent \u2014 the report updates here once the chat revises it", true );
			}
			else
				showTray( event.data.error || "Could not send the comments", true );
		} );

		addButton.addEventListener( "click", () => add() );
		cardSend.addEventListener( "click", () => ( add() || queue.length ) && send() );
		sendButton.addEventListener( "click", send );
		clearButton.addEventListener( "click", () => { clearQueue(); showTray(); } );
		field.addEventListener( "input", () => { field.style.height = "auto"; field.style.height = `${field.scrollHeight}px`; updateButtons(); } );
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
		const isControl = node => card.contains( node ) || tray.contains( node ) || panel.contains( node ) || node === cog;
		document.addEventListener( "pointermove", event => {
			if ( isSelecting || event.buttons || isControl( event.target ) )
				return hover( null );
			const figure = event.target.closest?.( figures );
			hover( figure && !figure.classList.contains( "loom-picked" ) && !event.target.closest( "a, .cite" ) ? figure : null );
		} );
		document.addEventListener( "pointerdown", event => {
			if ( isControl( event.target ) )
				return;
			isSelecting = true;
			hover( null );
			if ( !card.hidden && !field.value.trim() )
				close();
		} );
		document.addEventListener( "pointerup", event => {
			if ( !isSelecting )
				return;
			isSelecting = false;
			const selection = getSelection();
			const report = document.querySelector( ".report" );
			if ( selection.rangeCount && !selection.isCollapsed && report?.contains( selection.anchorNode ) && report.contains( selection.focusNode ) )
			{
				const range = selection.getRangeAt( 0 ).cloneRange();
				const rects = [...range.getClientRects()];
				open( { about: clip( selection.toString(), 300 ), range }, rects[rects.length - 1] ?? range.getBoundingClientRect() );
				return;
			}
			const figure = event.target.closest?.( figures );
			// Using an interactive piece, such as dragging a compare handle or picking a tab, is not a comment.
			if ( figure && !event.target.closest( "a, .cite, .compare, .tab-bar" ) )
				open( { about: describe( figure ), element: figure }, figure.getBoundingClientRect() );
		} );
		body.append( card, tray, chip );
	}
	body.append( panel, cog );

	// Compare: <div class="compare" data-a="Before" data-b="After"><img src="a"><img src="b"></div>. Dragging, clicking or the arrow keys move the
	// split; the second image shows right of it.
	for ( const compare of document.querySelectorAll( ".compare" ) )
	{
		const handle = document.createElement( "div" );
		handle.className = "compare-handle";
		compare.append( handle );
		for ( const side of ["a", "b"] )
		{
			if ( !compare.dataset[side] )
				continue;
			const label = document.createElement( "span" );
			label.className = "compare-label " + side;
			label.textContent = compare.dataset[side];
			compare.append( label );
		}
		compare.tabIndex = 0;
		compare.setAttribute( "role", "slider" );
		compare.setAttribute( "aria-label", "Compare " + ( compare.dataset.a || "first" ) + " and " + ( compare.dataset.b || "second" ) );
		const set = share => {
			share = Math.min( 1, Math.max( 0, share ) );
			compare.style.setProperty( "--split", share * 100 + "%" );
			compare.setAttribute( "aria-valuenow", Math.round( share * 100 ) );
			compare.dataset.split = share;
		};
		set( parseFloat( compare.dataset.start ?? "0.5" ) );
		const follow = event => {
			const box = compare.getBoundingClientRect();
			set( ( event.clientX - box.left ) / box.width );
		};
		compare.addEventListener( "pointerdown", event => {
			compare.setPointerCapture( event.pointerId );
			follow( event );
		} );
		compare.addEventListener( "pointermove", event => {
			if ( compare.hasPointerCapture( event.pointerId ) )
				follow( event );
		} );
		compare.addEventListener( "keydown", event => {
			const step = event.key === "ArrowLeft" ? -0.05 : event.key === "ArrowRight" ? 0.05 : 0;
			if ( step )
			{
				set( parseFloat( compare.dataset.split ) + step );
				event.preventDefault();
			}
		} );
	}

	// Tabs: <div class="tabs"><section data-tab="Before">…</section><section data-tab="After">…</section></div>. Flip: <div class="flip"><img
	// data-tab="A" src="a"><img data-tab="B" src="b"></div>, the same with a compact switch, for swapping images in place. Each gets a bar of
	// buttons; one child shows at a time.
	for ( const group of document.querySelectorAll( ".tabs, .flip" ) )
	{
		const panes = [...group.children].filter( child => child.dataset.tab !== undefined );
		if ( panes.length < 2 )
			continue;
		const bar = document.createElement( "div" );
		bar.className = "tab-bar";
		bar.setAttribute( "role", "tablist" );
		const buttons = panes.map( ( pane, index ) => {
			const button = document.createElement( "button" );
			button.type = "button";
			button.setAttribute( "role", "tab" );
			button.textContent = pane.dataset.tab || String.fromCharCode( 65 + index );
			button.addEventListener( "click", () => show( index ) );
			bar.append( button );
			return button;
		} );
		const show = index => panes.forEach( ( pane, at ) => {
			pane.hidden = at !== index;
			buttons[at].setAttribute( "aria-selected", at === index );
			buttons[at].tabIndex = at === index ? 0 : -1;
		} );
		bar.addEventListener( "keydown", event => {
			const at = buttons.indexOf( document.activeElement );
			const step = event.key === "ArrowRight" ? 1 : event.key === "ArrowLeft" ? -1 : 0;
			if ( at >= 0 && step )
			{
				const next = ( at + step + buttons.length ) % buttons.length;
				show( next );
				buttons[next].focus();
			}
		} );
		group.prepend( bar );
		show( Math.max( 0, panes.findIndex( pane => pane.hasAttribute( "data-selected" ) ) ) );
	}
} )();
