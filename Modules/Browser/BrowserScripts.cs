namespace Lokn.Modules.Browser;

/// <summary>Holds the scripts the browser tools run in pages, each a function called with one JSON argument.</summary>
internal static class BrowserScripts
{
	/// <summary>Runs in every page as it is created and tells Loom the color the page wants around it, as <c>{ "loomChrome": "#rrggbb" }</c>, or an empty
	/// string for none: the first <c>&lt;meta name="theme-color"&gt;</c> whose media matches, else the page's own opaque background. It tells again whenever
	/// that changes, such as when a report switches its paper.</summary>
	internal const string ChromeColor = """
		(() => {
			if (!window.chrome || !window.chrome.webview)
				return;
			const context = new OffscreenCanvas(1, 1).getContext('2d');
			const opaque = value => {
				if (!value)
					return '';
				context.fillStyle = '#000000';
				context.fillStyle = value;
				return /^#[0-9a-f]{6}$/.test(context.fillStyle) ? context.fillStyle : '';
			};
			const read = () => {
				const meta = [...document.querySelectorAll('meta[name="theme-color"]')].find(meta => !meta.media || matchMedia(meta.media).matches);
				if (meta)
					return opaque(meta.content.trim());
				return (document.body && opaque(getComputedStyle(document.body).backgroundColor)) || opaque(getComputedStyle(document.documentElement).backgroundColor);
			};
			let last = null, pending = 0;
			const update = () => {
				pending = 0;
				const color = read();
				if (color !== last)
					window.chrome.webview.postMessage({ loomChrome: last = color });
			};
			const schedule = () => pending || (pending = setTimeout(update, 50));
			new MutationObserver(schedule).observe(document, { subtree: true, childList: true, attributes: true, attributeFilter: ['content', 'media', 'style', 'class', 'data-paper', 'data-theme'] });
			matchMedia('(prefers-color-scheme: dark)').addEventListener('change', schedule);
			addEventListener('DOMContentLoaded', schedule);
			addEventListener('load', schedule);
		})();
		""";

	/// <summary>Lists the page's title, address, scroll position, a slice of its text, and its visible interactive elements, numbering each with a <c>data-lokn-ref</c> attribute for later actions.</summary>
	/// <remarks>Takes <c>{ offset, max, limit }</c>: where the text slice starts, its length, and how many elements to number.</remarks>
	internal const string Read = """
		(({ offset, max, limit }) => {
			for (const old of document.querySelectorAll('[data-lokn-ref]'))
				old.removeAttribute('data-lokn-ref');
			const selector = 'a[href],button,input:not([type=hidden]),textarea,select,summary,[contenteditable=""],[contenteditable=true],[onclick],'
				+ '[role=button],[role=link],[role=checkbox],[role=radio],[role=switch],[role=tab],[role=menuitem],[role=menuitemcheckbox],[role=option],[role=combobox],[role=textbox],[role=searchbox],[role=slider],'
				+ '[tabindex]:not([tabindex="-1"])';
			const clean = text => (text || '').replace(/\s+/g, ' ').trim();
			const clip = (text, length) => text.length > length ? text.slice(0, length - 1) + '…' : text;
			const isVisible = element => {
				if (!element.getClientRects().length)
					return false;
				const style = getComputedStyle(element);
				return style.visibility !== 'hidden' && style.visibility !== 'collapse';
			};
			const roleOf = element => {
				const role = element.getAttribute('role');
				if (role)
					return role;
				const tag = element.tagName.toLowerCase();
				if (tag === 'input') {
					const type = (element.type || 'text').toLowerCase();
					if (type === 'checkbox' || type === 'radio')
						return type;
					if (['button', 'submit', 'reset', 'image', 'file', 'color'].includes(type))
						return 'button';
					return type === 'range' ? 'slider' : 'textbox';
				}
				return { a: 'link', button: 'button', select: 'combobox', textarea: 'textbox', summary: 'button' }[tag]
					|| (element.isContentEditable ? 'textbox' : 'clickable ' + tag);
			};
			const nameOf = element => {
				const labelledBy = element.getAttribute('aria-labelledby');
				const candidates = [
					element.getAttribute('aria-label'),
					labelledBy && labelledBy.split(/\s+/).map(id => document.getElementById(id)?.innerText || '').join(' '),
					element.labels && element.labels[0]?.innerText,
					element.tagName === 'SELECT' ? '' : element.innerText,
					element.placeholder,
					element.alt,
					element.querySelector && element.querySelector('img[alt]')?.alt,
					element.title,
					['button', 'submit', 'reset'].includes(element.type) ? element.value : '',
				];
				for (const candidate of candidates) {
					const text = clean(candidate);
					if (text)
						return clip(text, 80);
				}
				return '';
			};
			const elements = [];
			let total = 0;
			for (const element of document.querySelectorAll(selector)) {
				if (!isVisible(element))
					continue;
				total++;
				if (elements.length >= limit)
					continue;
				const ref = elements.length + 1;
				element.setAttribute('data-lokn-ref', String(ref));
				const role = roleOf(element);
				const item = { ref, role, name: nameOf(element) };
				if (element.tagName === 'A' && element.href)
					item.href = clip(element.href, 120);
				if (element.tagName === 'SELECT')
					item.value = clip(clean(element.selectedOptions[0]?.text), 80);
				else if ((role === 'textbox' || role === 'slider' || role === 'searchbox' || role === 'combobox') && 'value' in element)
					item.value = clip(String(element.value), 80);
				if (element.checked || element.getAttribute('aria-checked') === 'true')
					item.checked = true;
				if (element.disabled || element.getAttribute('aria-disabled') === 'true')
					item.disabled = true;
				const box = element.getBoundingClientRect();
				if (box.bottom < 0 || box.top > innerHeight)
					item.offscreen = true;
				elements.push(item);
			}
			const text = (document.body ? document.body.innerText : '').replace(/\n{3,}/g, '\n\n');
			return {
				title: document.title,
				url: location.href,
				elements,
				elementCount: total,
				text: text.slice(offset, offset + max),
				textLength: text.length,
				scrollY: Math.round(scrollY),
				scrollHeight: document.documentElement.scrollHeight,
				viewportHeight: innerHeight,
			};
		})
		""";

	/// <summary>Finds an element by the ref <see cref="Read"/> gave it or by a CSS selector, scrolls it to the middle of the viewport, and returns its center and name.</summary>
	/// <remarks>
	/// Takes <c>{ ref, selector, focus, clear, option }</c>. With <c>focus</c> it focuses the element, and with <c>clear</c> also selects its
	/// content so typing replaces it. With <c>option</c> it chooses that option of a select box by value or text. Returns <c>{ error }</c>
	/// when it cannot, and names the element covering the center, if any, as <c>covered</c>.
	/// </remarks>
	internal const string Locate = """
		(target => {
			const describe = element => {
				const text = (element.getAttribute('aria-label') || (element.labels && element.labels[0]?.innerText) || element.placeholder || element.innerText || element.title || element.value || '').replace(/\s+/g, ' ').trim();
				return element.tagName.toLowerCase() + (text ? ' "' + (text.length > 60 ? text.slice(0, 59) + '…' : text) + '"' : '');
			};
			let element;
			try {
				element = target.ref ? document.querySelector('[data-lokn-ref="' + target.ref + '"]') : document.querySelector(target.selector);
			} catch (error) {
				return { error: 'Invalid selector: ' + error.message };
			}
			if (!element)
				return { error: target.ref ? 'No element has ref ' + target.ref + '; the page changed since browser_read, so read it again.' : 'No element matches ' + target.selector + '.' };
			element.scrollIntoView({ block: 'center', inline: 'center', behavior: 'instant' });
			if (target.option !== undefined && target.option !== null) {
				if (element.tagName !== 'SELECT')
					return { error: describe(element) + ' is not a select box; click it and then the option instead.' };
				const wanted = target.option.trim().toLowerCase();
				const option = [...element.options].find(item => item.value.toLowerCase() === wanted || item.text.trim().toLowerCase() === wanted);
				if (!option)
					return { error: 'No option ' + target.option + '. Options: ' + [...element.options].map(item => item.text.trim()).join(', ') };
				element.value = option.value;
				element.dispatchEvent(new Event('input', { bubbles: true }));
				element.dispatchEvent(new Event('change', { bubbles: true }));
				return { name: describe(element), chosen: option.text.trim() };
			}
			const box = element.getBoundingClientRect();
			if (box.width === 0 && box.height === 0)
				return { error: describe(element) + ' has no size on screen.' };
			const x = box.left + box.width / 2;
			const y = box.top + box.height / 2;
			const hit = document.elementFromPoint(x, y);
			const covered = hit && hit !== element && !element.contains(hit) && !hit.contains(element) ? describe(hit) : null;
			if (target.focus) {
				element.focus();
				if (target.clear) {
					if (typeof element.select === 'function') {
						element.select();
					} else if (element.isContentEditable) {
						const range = document.createRange();
						range.selectNodeContents(element);
						const selection = getSelection();
						selection.removeAllRanges();
						selection.addRange(range);
					}
				}
			}
			return { x, y, name: describe(element), covered };
		})
		""";

	/// <summary>Scrolls the page by most of a viewport and returns where it is.</summary>
	/// <remarks>Takes <c>{ direction }</c>: 1 scrolls down, -1 up.</remarks>
	internal const string Scroll = """
		(({ direction }) => {
			window.scrollBy({ top: direction * innerHeight * 0.8, behavior: 'instant' });
			return { scrollY: Math.round(scrollY), scrollHeight: document.documentElement.scrollHeight, viewportHeight: innerHeight };
		})
		""";
}
