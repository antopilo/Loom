// Loom's service worker: shows the host's web push notifications and opens their chat when one is clicked.

self.addEventListener('install', () => self.skipWaiting());

self.addEventListener('activate', event => event.waitUntil(self.clients.claim()));

// Names a chat the way the page's address does (a GUID in N form), or null for anything else a push carries.
function readChat(value) {
	return typeof value === 'string' && /^[0-9a-f]{32}$/i.test(value) ? value.toLowerCase() : null;
}

self.addEventListener('push', event => {
	let data = {};
	try {
		data = event.data ? event.data.json() : {};
	} catch {
		data = { body: event.data ? event.data.text() : '' };
	}
	const chat = readChat(data.ch);
	event.waitUntil(self.registration.showNotification(typeof data.title === 'string' && data.title ? data.title : 'Loom', {
		body: typeof data.body === 'string' ? data.body : '',
		tag: chat || 'loom',
		renotify: true,
		icon: 'icon.png',
		badge: 'icon.png',
		data: { ch: chat },
	}));
});

self.addEventListener('notificationclick', event => {
	event.notification.close();
	const chat = readChat(event.notification.data ? event.notification.data.ch : null);
	const scope = self.registration.scope;
	const target = new URL(chat ? './#chat=' + chat : './', scope).href;
	event.waitUntil((async () => {
		// A relay origin serves several hosts' pages under their own paths; only a page of this worker's host may open its chat.
		const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
		for (const client of windows) {
			if (client.url.startsWith(scope) && 'focus' in client) {
				if (chat) {
					client.postMessage({ open: chat });
				}
				return client.focus();
			}
		}
		return self.clients.openWindow(target);
	})());
});
