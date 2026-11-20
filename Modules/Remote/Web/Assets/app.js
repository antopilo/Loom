// Loom's web client: pairs with a Loom host, connects over the secure channel of docs/remote.md and shows its chats.
// Plain ES2022 with no build step and nothing loaded from elsewhere; the host serves this file as is.

const PROTOCOL_VERSION = 2;
const HANDSHAKE_MS = 15000;
const MAX_RETRY_MS = 15000;
const CACHED_CHATS = 8;

const utf8 = new TextEncoder();
const fromUtf8 = new TextDecoder();

// ---------- Bytes and ids ----------

function encodeBase64Url(bytes) {
	let binary = '';
	for (const value of new Uint8Array(bytes)) {
		binary += String.fromCharCode(value);
	}
	return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function decodeBase64Url(text) {
	const base64 = String(text).replace(/-/g, '+').replace(/_/g, '/');
	const binary = atob(base64 + '==='.slice((base64.length + 3) % 4));
	const bytes = new Uint8Array(binary.length);
	for (let index = 0; index < binary.length; index++) {
		bytes[index] = binary.charCodeAt(index);
	}
	return bytes;
}

function concatBytes(...parts) {
	const joined = new Uint8Array(parts.reduce((sum, part) => sum + part.length, 0));
	let offset = 0;
	for (const part of parts) {
		joined.set(part, offset);
		offset += part.length;
	}
	return joined;
}

function sameBytes(buffer, bytes) {
	if (!buffer) {
		return false;
	}
	const other = new Uint8Array(buffer);
	return other.length === bytes.length && other.every((value, index) => value === bytes[index]);
}

async function sha256(bytes) {
	return new Uint8Array(await crypto.subtle.digest('SHA-256', bytes));
}

async function computeHostId(spki) {
	return encodeBase64Url(await sha256(spki)).slice(0, 22);
}

function newGuid() {
	return crypto.randomUUID().replaceAll('-', '');
}

// Frames name chats by GUIDs in N form; store objects carry them in D form.
function toGuidN(id) {
	return id ? String(id).replaceAll('-', '').toLowerCase() : null;
}

function toGuidD(id) {
	return `${id.slice(0, 8)}-${id.slice(8, 12)}-${id.slice(12, 16)}-${id.slice(16, 20)}-${id.slice(20)}`;
}

class RemoteError extends Error {
	constructor(code, message) {
		super(message);
		this.code = code;
	}
}

// ---------- Saved keys (IndexedDB keeps the non-extractable private key) ----------

let databasePromise = null;

function openDatabase() {
	databasePromise ??= new Promise((resolve, reject) => {
		const request = indexedDB.open('loom', 1);
		request.onupgradeneeded = () => request.result.createObjectStore('keys');
		request.onsuccess = () => resolve(request.result);
		request.onerror = () => reject(request.error);
	});
	return databasePromise;
}

async function runStore(mode, action) {
	const database = await openDatabase();
	return new Promise((resolve, reject) => {
		const transaction = database.transaction('keys', mode);
		const request = action(transaction.objectStore('keys'));
		transaction.oncomplete = () => resolve(request.result ?? null);
		transaction.onerror = () => reject(transaction.error);
	});
}

const readSaved = key => runStore('readonly', store => store.get(key));
const writeSaved = (key, value) => runStore('readwrite', store => store.put(value, key));
const deleteSaved = key => runStore('readwrite', store => store.delete(key));

// Paired hosts are kept as host:<hostId>, so several hosts behind one relay origin keep their own record.
const hostKey = hostId => 'host:' + hostId;

// Names the host a relay page at <prefix>/h/<hostId>/ belongs to, or null on a host's own origin.
function pageHostId() {
	return /\/h\/([A-Za-z0-9_-]+)\//.exec(location.pathname)?.[1] ?? null;
}

// Reads the paired host this page talks to: the relay page's own host, or else the most recently paired one.
async function readHost() {
	const relayed = pageHostId();
	if (relayed) {
		return readSaved(hostKey(relayed));
	}
	const hosts = await runStore('readonly', store => store.getAll(IDBKeyRange.bound('host:', 'host:\uffff')));
	return hosts.reduce((latest, host) => !latest || (host.paired ?? 0) > (latest.paired ?? 0) ? host : latest, null);
}

const writeHost = host => writeSaved(hostKey(host.hostId), { ...host, paired: Date.now() });

// The device's static key pair; the private key can be used but never read back.
async function ensureDevice() {
	const saved = await readSaved('device');
	if (saved) {
		return saved;
	}
	const pair = await crypto.subtle.generateKey({ name: 'ECDH', namedCurve: 'P-256' }, false, ['deriveBits']);
	const spki = new Uint8Array(await crypto.subtle.exportKey('spki', pair.publicKey));
	const device = { id: newGuid(), key: pair.privateKey, publicKey: encodeBase64Url(spki) };
	await writeSaved('device', device);
	return device;
}

// ---------- Socket ----------

// The host serves the page at its root and listens on /loom; a relay serves it at <prefix>/h/<hostId>/ and carries the socket on <prefix>/device/<hostId>.
function socketUrl() {
	const origin = (location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host;
	const relayed = /^(.*)\/h\/([A-Za-z0-9_-]+)\//.exec(location.pathname);
	return origin + (relayed ? relayed[1] + '/device/' + relayed[2] : '/loom');
}

// Wraps a WebSocket: messages queue until read with receive(), or go to a listener once one is set.
class Socket {
	#socket;
	#queue = [];
	#waiters = [];
	#onMessage = null;
	#onClose = null;
	#closed = false;

	static open(url) {
		return new Promise((resolve, reject) => {
			const socket = new WebSocket(url);
			socket.binaryType = 'arraybuffer';
			const wrapper = new Socket(socket);
			socket.onopen = () => resolve(wrapper);
			socket.onerror = () => reject(new RemoteError('offline', 'Loom cannot reach the host.'));
		});
	}

	constructor(socket) {
		this.#socket = socket;
		socket.onmessage = event => this.#deliver(event.data);
		socket.onclose = () => this.#close();
	}

	receive(timeout) {
		if (this.#queue.length > 0) {
			return Promise.resolve(this.#queue.shift());
		}
		if (this.#closed) {
			return Promise.resolve(null);
		}
		return new Promise((resolve, reject) => {
			const waiter = { resolve, timer: 0 };
			waiter.timer = setTimeout(() => {
				this.#waiters.splice(this.#waiters.indexOf(waiter), 1);
				reject(new RemoteError('timeout', 'The host did not answer in time.'));
			}, timeout);
			this.#waiters.push(waiter);
		});
	}

	listen(onMessage, onClose) {
		this.#onMessage = onMessage;
		this.#onClose = onClose;
		for (const data of this.#queue.splice(0)) {
			onMessage(data);
		}
		if (this.#closed) {
			onClose();
		}
	}

	sendText(frame) {
		this.#socket.send(JSON.stringify(frame));
	}

	sendBinary(bytes) {
		this.#socket.send(bytes);
	}

	close() {
		this.#socket.close();
	}

	#deliver(data) {
		if (this.#onMessage) {
			this.#onMessage(data);
		} else if (this.#waiters.length > 0) {
			const waiter = this.#waiters.shift();
			clearTimeout(waiter.timer);
			waiter.resolve(data);
		} else {
			this.#queue.push(data);
		}
	}

	#close() {
		if (this.#closed) {
			return;
		}
		this.#closed = true;
		for (const waiter of this.#waiters.splice(0)) {
			clearTimeout(waiter.timer);
			waiter.resolve(null);
		}
		this.#onClose?.();
	}
}

function readText(data) {
	if (data === null) {
		throw new RemoteError('closed', 'The host closed the connection.');
	}
	if (typeof data !== 'string') {
		throw new RemoteError('bad-request', 'The host sent an unexpected message.');
	}
	const frame = JSON.parse(data);
	if (frame.t === 'err') {
		throw new RemoteError(frame.code ?? 'failed', frame.message ?? 'The host refused the connection.');
	}
	return frame;
}

// ---------- Secure channel ----------

function buildNonce(counter) {
	const nonce = new Uint8Array(12);
	new DataView(nonce.buffer).setBigUint64(4, counter, true);
	return nonce;
}

// Each message: counter (8 bytes, little-endian) ‖ AES-256-GCM ciphertext ‖ tag; counters start at 0 and rise by one per direction.
class SecureChannel {
	#socket;
	#sendKey;
	#receiveKey;
	#sendCounter = 0n;
	#receiveCounter = 0n;
	#sending = Promise.resolve();
	#receiving = Promise.resolve();
	#failed = false;

	constructor(socket, sendKey, receiveKey) {
		this.#socket = socket;
		this.#sendKey = sendKey;
		this.#receiveKey = receiveKey;
	}

	send(frame) {
		const counter = this.#sendCounter++;
		const plaintext = utf8.encode(JSON.stringify(frame));
		// Encryption is asynchronous, so a chain keeps the messages in counter order.
		const task = this.#sending.then(async () => {
			const sealed = new Uint8Array(await crypto.subtle.encrypt({ name: 'AES-GCM', iv: buildNonce(counter), tagLength: 128 }, this.#sendKey, plaintext));
			const message = new Uint8Array(8 + sealed.length);
			new DataView(message.buffer).setBigUint64(0, counter, true);
			message.set(sealed, 8);
			this.#socket.sendBinary(message);
		});
		this.#sending = task.catch(() => {});
		return task;
	}

	async open(data) {
		if (!(data instanceof ArrayBuffer) || data.byteLength < 24) {
			throw new RemoteError('failed', 'A message is not an encrypted frame.');
		}
		const counter = new DataView(data).getBigUint64(0, true);
		if (counter !== this.#receiveCounter) {
			throw new RemoteError('failed', `A message carries counter ${counter} where ${this.#receiveCounter} was due.`);
		}
		let plaintext;
		try {
			plaintext = await crypto.subtle.decrypt({ name: 'AES-GCM', iv: buildNonce(counter), tagLength: 128 }, this.#receiveKey, new Uint8Array(data, 8));
		} catch {
			throw new RemoteError('failed', 'A message failed to decrypt.');
		}
		this.#receiveCounter++;
		return JSON.parse(fromUtf8.decode(plaintext));
	}

	listen(onFrame, onClose) {
		this.#socket.listen(data => {
			this.#receiving = this.#receiving.then(async () => {
				if (this.#failed) {
					return;
				}
				try {
					onFrame(await this.open(data));
				} catch (error) {
					// A broken message ends the connection, as on the host.
					this.#failed = true;
					console.error(error);
					this.#socket.close();
				}
			});
		}, onClose);
	}

	close() {
		this.#socket.close();
	}
}

const importPublicKey = bytes => crypto.subtle.importKey('spki', bytes, { name: 'ECDH', namedCurve: 'P-256' }, false, []);

async function deriveSecret(publicKey, privateKey) {
	return new Uint8Array(await crypto.subtle.deriveBits({ name: 'ECDH', public: publicKey }, privateKey, 256));
}

// The connect handshake: hello both ways, 3DH, HKDF-SHA256 into one key per direction, then the encrypted ready frames. The salt binds both hello
// frames as sent, so nobody between the two can change them. A host record from a pairing names the device id it paired under.
async function runHandshake(socket, device, host) {
	const ephemeral = await crypto.subtle.generateKey({ name: 'ECDH', namedCurve: 'P-256' }, false, ['deriveBits']);
	const ephemeralKey = new Uint8Array(await crypto.subtle.exportKey('spki', ephemeral.publicKey));
	const hello = { t: 'hello', v: PROTOCOL_VERSION, deviceId: host.deviceId ?? device.id, eph: encodeBase64Url(ephemeralKey) };
	const helloBytes = utf8.encode(JSON.stringify(hello));
	socket.sendText(hello);

	const answerData = await socket.receive(HANDSHAKE_MS);
	const answer = readText(answerData);
	const answerBytes = utf8.encode(answerData);
	if (answer.t !== 'hello') {
		throw new RemoteError('bad-request', `The host answered ${answer.t} where hello was due.`);
	}
	if (answer.v !== PROTOCOL_VERSION) {
		throw new RemoteError('version', 'The host speaks another protocol version. Reload this page.');
	}
	const hostKey = decodeBase64Url(host.hostKey);
	if (answer.hostId !== host.hostId || await computeHostId(hostKey) !== host.hostId) {
		throw new RemoteError('failed', 'The host is not the paired host.');
	}
	const hostEphemeralKey = decodeBase64Url(answer.eph);
	const hostStatic = await importPublicKey(hostKey);
	const hostEphemeral = await importPublicKey(hostEphemeralKey);
	const ikm = concatBytes(
		await deriveSecret(hostEphemeral, ephemeral.privateKey),
		await deriveSecret(hostEphemeral, device.key),
		await deriveSecret(hostStatic, ephemeral.privateKey));
	const salt = await sha256(concatBytes(ephemeralKey, hostEphemeralKey, await sha256(helloBytes), await sha256(answerBytes)));
	const material = await crypto.subtle.importKey('raw', ikm, 'HKDF', false, ['deriveKey']);
	const deriveKey = (info, usage) => crypto.subtle.deriveKey(
		{ name: 'HKDF', hash: 'SHA-256', salt, info: utf8.encode(info) }, material, { name: 'AES-GCM', length: 256 }, false, [usage]);
	const channel = new SecureChannel(socket, await deriveKey('loom c2h v1', 'encrypt'), await deriveKey('loom h2c v1', 'decrypt'));

	await channel.send({ t: 'ready' });
	const reply = await socket.receive(HANDSHAKE_MS);
	if (reply === null) {
		throw new RemoteError('closed', 'The host closed the connection during the handshake.');
	}
	if ((await channel.open(reply)).t !== 'ready') {
		throw new RemoteError('bad-request', 'The host did not confirm the connection.');
	}
	return { channel, scopes: Array.isArray(answer.scopes) ? answer.scopes : [] };
}

// ---------- Pairing ----------

async function readPairingLink(payload) {
	try {
		const data = JSON.parse(fromUtf8.decode(decodeBase64Url(payload)));
		if (typeof data.hostId !== 'string' || typeof data.hostKey !== 'string' || typeof data.secret !== 'string') {
			return null;
		}
		if (decodeBase64Url(data.secret).length !== 32 || await computeHostId(decodeBase64Url(data.hostKey)) !== data.hostId) {
			return null;
		}
		return data;
	} catch {
		return null;
	}
}

// Each pairing uses a new device id, kept in the host's record: a host refuses to pair an id it already knows.
async function pairDevice(link, name) {
	const device = await ensureDevice();
	const deviceId = newGuid();
	const socket = await Socket.open(socketUrl());
	try {
		const secret = await crypto.subtle.importKey('raw', decodeBase64Url(link.secret), { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
		const mac = await crypto.subtle.sign('HMAC', secret, utf8.encode(link.hostId + '.' + device.publicKey));
		socket.sendText({ t: 'pair', deviceId, name, deviceKey: device.publicKey, mac: encodeBase64Url(mac) });
		const answer = readText(await socket.receive(HANDSHAKE_MS));
		if (answer.t !== 'paired' || answer.hostId !== link.hostId) {
			throw new RemoteError('failed', 'The host is not the one the pairing link names.');
		}
		await writeHost({ hostId: link.hostId, hostKey: link.hostKey, deviceId });
	} finally {
		socket.close();
	}
}

function guessDeviceName() {
	const agent = navigator.userAgent;
	if (/iPhone/.test(agent)) {
		return 'iPhone';
	}
	if (/iPad/.test(agent) || (/Macintosh/.test(agent) && navigator.maxTouchPoints > 1)) {
		return 'iPad';
	}
	if (/Android/.test(agent)) {
		return /Mobile/.test(agent) ? 'Android phone' : 'Android tablet';
	}
	if (/Macintosh/.test(agent)) {
		return 'Mac browser';
	}
	if (/Windows/.test(agent)) {
		return 'Windows browser';
	}
	return 'Browser';
}

// ---------- Requests and pushes ----------

class Rpc {
	#channel;
	#nextId = 0;
	#pending = new Map();
	#pushes = new Map();
	closed = false;

	constructor(channel) {
		this.#channel = channel;
	}

	on(type, handler) {
		this.#pushes.set(type, handler);
	}

	request(type, fields = {}) {
		if (this.closed) {
			return Promise.reject(new RemoteError('closed', 'Not connected to the host.'));
		}
		const id = ++this.#nextId;
		return new Promise((resolve, reject) => {
			this.#pending.set(id, { resolve, reject });
			this.#channel.send({ ...fields, t: type, id }).catch(error => {
				this.#pending.delete(id);
				reject(error);
			});
		});
	}

	dispatch(frame) {
		if ((frame.t === 'ok' || frame.t === 'err') && typeof frame.re === 'number') {
			const pending = this.#pending.get(frame.re);
			if (pending) {
				this.#pending.delete(frame.re);
				if (frame.t === 'ok') {
					pending.resolve(frame);
				} else {
					pending.reject(new RemoteError(frame.code ?? 'failed', frame.message ?? 'The request failed.'));
				}
			}
			return;
		}
		if (typeof frame.id === 'number') {
			this.#channel.send({ t: 'err', re: frame.id, code: 'bad-request', message: `The web client does not answer ${frame.t}.` });
			return;
		}
		this.#pushes.get(frame.t)?.(frame);
	}

	close() {
		this.closed = true;
		this.#channel.close();
		for (const pending of this.#pending.values()) {
			pending.reject(new RemoteError('closed', 'The connection closed.'));
		}
		this.#pending.clear();
	}
}

// ---------- State ----------

const state = {
	ready: false,
	paired: false,
	revoked: false,
	pairing: null,
	pairError: '',
	rpc: null,
	connected: false,
	everConnected: false,
	// Raised whenever the page drops its host, such as after pairing with another, so a connection to the earlier one under way is abandoned.
	epoch: 0,
	hostId: null,
	scopes: [],
	hostName: '',
	boot: null,
	projects: [],
	conversations: new Map(),
	working: new Set(),
	browserTabs: new Map(),
	profile: null,
	chat: null,
	chats: new Map(),
	steer: false,
	pushOn: false,
	retryDelay: 1000,
	retryTimer: 0,
	connecting: false,
	syncTimer: 0,
};

// A relay origin serves the pages of several hosts, so what the page remembers outside IndexedDB is kept per host.
const profileKey = hostId => 'loom.profile.' + hostId;

// Takes the host this page talks to, with the account profile the user last chose for it.
function useHost(hostId) {
	if (state.hostId !== hostId) {
		state.hostId = hostId;
		state.profile = hostId ? localStorage.getItem(profileKey(hostId)) : null;
	}
}

const canControl = () => state.scopes.includes('control');

// The account profiles the host's chats and projects use; the list shows one at a time.
function listProfiles() {
	return ['Work', 'Personal'].filter(profile => [...state.conversations.values()].some(info => info.hasChat !== false && info.profile === profile)
		|| state.projects.some(project => project.profile === profile));
}

function currentProfile() {
	const profiles = listProfiles();
	return profiles.includes(state.profile) ? state.profile : (profiles[0] ?? 'Work');
}

function scheduleRetry() {
	clearTimeout(state.retryTimer);
	if (document.hidden || !state.paired) {
		return;
	}
	state.retryTimer = setTimeout(connect, state.retryDelay);
	state.retryDelay = Math.min(state.retryDelay * 2, MAX_RETRY_MS);
}

async function connect() {
	if (state.connecting || state.rpc) {
		return;
	}
	state.connecting = true;
	clearTimeout(state.retryTimer);
	const epoch = state.epoch;
	let socket = null;
	let host = null;
	try {
		const device = await ensureDevice();
		host = await readHost();
		if (epoch !== state.epoch) {
			return;
		}
		state.paired = Boolean(host);
		if (!host) {
			render();
			return;
		}
		useHost(host.hostId);
		showBanner(state.everConnected ? 'Reconnecting…' : 'Connecting…');
		socket = await Socket.open(socketUrl());
		const { channel, scopes } = await runHandshake(socket, device, host);
		if (epoch !== state.epoch) {
			// The page paired with another host meanwhile.
			socket.close();
			return;
		}
		const rpc = new Rpc(channel);
		registerPushes(rpc);
		state.rpc = rpc;
		state.scopes = scopes;
		channel.listen(frame => rpc.dispatch(frame), () => handleDisconnect(rpc));
		await syncWith(rpc);
		if (state.rpc !== rpc) {
			return;
		}
		state.connected = true;
		state.everConnected = true;
		state.retryDelay = 1000;
		showBanner(null);
		render();
		// A hidden page stays detached, so the host sends this device web pushes; showing it attaches the chat.
		if (state.chat && !document.hidden) {
			attachChat(state.chat);
		}
		refreshPush();
	} catch (error) {
		socket?.close();
		if (epoch !== state.epoch) {
			return;
		}
		if (error.code === 'revoked') {
			// The answer is not authenticated, so the pairing stays; the page only says what the host answered.
			state.paired = false;
			state.revoked = true;
			showBanner(null);
			render();
			return;
		}
		showBanner(error.message, true);
		scheduleRetry();
	} finally {
		state.connecting = false;
		if (epoch !== state.epoch) {
			// The page dropped this host while connecting to it; it connects to the one it now has.
			connect();
		}
	}
}

// Drops the connection and everything the page holds of its host, such as before talking to a host it just paired with.
function dropHost() {
	state.epoch++;
	clearTimeout(state.retryTimer);
	clearTimeout(state.syncTimer);
	const rpc = state.rpc;
	state.rpc = null;
	rpc?.close();
	state.connected = false;
	state.scopes = [];
	state.hostName = '';
	state.boot = null;
	state.projects = [];
	state.conversations = new Map();
	state.working = new Set();
	state.browserTabs = new Map();
	state.chat = null;
	state.chats.clear();
	state.pushOn = false;
	state.retryDelay = 1000;
	state.everConnected = false;
	useHost(null);
}

function handleDisconnect(rpc) {
	if (state.rpc !== rpc) {
		return;
	}
	rpc.close();
	state.rpc = null;
	state.connected = false;
	if (state.chat) {
		state.chat.attached = false;
		state.chat.buffer = null;
	}
	showBanner('Reconnecting…');
	renderHeader();
	renderSession();
	scheduleRetry();
}

function registerPushes(rpc) {
	rpc.on('store', frame => receiveListPush(rpc, frame));
	rpc.on('working', frame => receiveListPush(rpc, frame));
	for (const type of ['browser.tab', 'browser.closed']) {
		rpc.on(type, frame => {
			// Tab pushes may come before the browser snapshot; they wait for it, and only tabs newer than its own apply.
			if (rpc.browserHeld) {
				rpc.browserHeld.push(frame);
			} else {
				applyBrowserPush(frame);
			}
		});
	}
	for (const type of ['state', 'msg', 'msgs', 'tool', 'evt']) {
		rpc.on(type, frame => {
			const chat = state.chat;
			if (!chat || chat.id !== toGuidN(frame.ch)) {
				return;
			}
			// Pushes may come before the attach answer; they wait for it and only newer ones apply.
			if (chat.buffer) {
				chat.buffer.push(frame);
			} else if (chat.attached) {
				applyChatPush(chat, frame);
			}
		});
	}
}

// Store and working pushes wait while a sync is under way, so its snapshot cannot overwrite them; afterwards only those newer than the
// snapshot's revision apply.
function receiveListPush(rpc, frame) {
	if (rpc.held) {
		rpc.held.push(frame);
		return;
	}
	applyListPush(frame);
}

function applyListPush(frame) {
	if (frame.t !== 'working') {
		applyStoreOp(frame.op);
		return;
	}
	const id = toGuidN(frame.ch);
	if (frame.busy) {
		state.working.add(id);
	} else {
		state.working.delete(id);
	}
	renderListSoon();
}

// One sync at a time per connection: overlapping ones would share the held pushes, and an earlier answer could apply pushes that a later
// snapshot then overwrites.
function syncWith(rpc) {
	const run = (rpc.syncing ?? Promise.resolve()).catch(() => {}).then(() => runSync(rpc));
	rpc.syncing = run;
	return run;
}

async function runSync(rpc) {
	rpc.held = [];
	rpc.browserHeld = [];
	try {
		const snapshot = await rpc.request('sync');
		if (state.rpc !== rpc) {
			return;
		}
		applySync(snapshot);
		const revision = snapshot.rev ?? -1;
		const held = rpc.held;
		rpc.held = null;
		for (const frame of held) {
			if ((frame.rev ?? Infinity) > revision) {
				applyListPush(frame);
			}
		}
		await syncBrowser(rpc);
	} finally {
		rpc.held = null;
		rpc.browserHeld = null;
	}
}

// The session's browser tabs, which this page lists but cannot show: pages open in Loom on a computer.
async function syncBrowser(rpc) {
	let snapshot;
	try {
		snapshot = await rpc.request('browser.sync');
	} catch (error) {
		if (error instanceof RemoteError) {
			// A host without browser tabs.
			return;
		}
		throw error;
	}
	state.browserTabs = new Map((snapshot.tabs ?? []).map(tab => [toGuidN(tab.id), tab]));
	const held = rpc.browserHeld ?? [];
	rpc.browserHeld = null;
	for (const frame of held) {
		applyBrowserPush(frame);
	}
}

function applyBrowserPush(frame) {
	if (frame.t === 'browser.closed') {
		state.browserTabs.delete(toGuidN(frame.tab));
	} else if (frame.tab && typeof frame.tab === 'object') {
		const id = toGuidN(frame.tab.id);
		const known = state.browserTabs.get(id);
		if (!known || (frame.tab.rev ?? 0) > (known.rev ?? 0)) {
			state.browserTabs.set(id, frame.tab);
		}
	}
	renderListSoon();
}

function applySync(snapshot) {
	state.projects = Array.isArray(snapshot.projects) ? snapshot.projects : [];
	state.conversations = new Map((snapshot.conversations ?? []).map(info => [toGuidN(info.id), info]));
	state.working = new Set((snapshot.working ?? []).map(toGuidN));
	state.hostName = snapshot.hostName ?? '';
	if (state.boot !== null && snapshot.boot !== state.boot) {
		// Another host process: sequence numbers start over, so cached chats load again in full.
		state.chats.clear();
		if (state.chat) {
			state.chat.seq = -1;
		}
	}
	state.boot = snapshot.boot ?? null;
}

function requestSync() {
	clearTimeout(state.syncTimer);
	state.syncTimer = setTimeout(async () => {
		const rpc = state.rpc;
		if (!rpc) {
			return;
		}
		try {
			await syncWith(rpc);
			renderList();
			renderHeader();
		} catch {
			// A closed connection syncs again when it reconnects.
		}
	}, 300);
}

function applyStoreOp(op) {
	const args = op?.a ?? {};
	if (op?.s === 'projects') {
		const id = toGuidN(args.id);
		const index = state.projects.findIndex(project => toGuidN(project.id) === id);
		if (op.op === 'Create' && index < 0) {
			state.projects.push({ id: args.id, profile: args.profile, name: args.name, directory: args.directory });
		} else if (op.op === 'Update' && index >= 0) {
			state.projects[index] = { ...state.projects[index], name: args.name, directory: args.directory };
		} else if (op.op === 'Delete') {
			if (index >= 0) {
				state.projects.splice(index, 1);
			}
		} else {
			requestSync();
		}
	} else if (op?.s === 'conversations') {
		const id = toGuidN(args.id);
		const info = state.conversations.get(id);
		const update = changes => {
			if (info) {
				state.conversations.set(id, { ...info, ...changes });
			}
		};
		switch (op.op) {
			case 'CreateConversation':
				if (!info) {
					state.conversations.set(id, {
						id: args.id, profile: args.profile, projectId: args.projectId ?? null, title: (args.title ?? '').trim() || 'New chat',
						updatedAt: args.createdAt, createdAt: args.createdAt, hasChat: args.hasChat ?? true, isPinned: false, position: null,
					});
				}
				break;
			case 'AddChat':
				update({ hasChat: true });
				break;
			case 'RenameConversation':
			case 'SetAutomaticTitle':
				update({ title: args.title });
				break;
			case 'UpdateConversation':
				update(toGuidN(info?.projectId) === toGuidN(args.projectId)
					? { title: args.title }
					: { title: args.title, projectId: args.projectId ?? null, position: null });
				break;
			case 'SetPinned':
				update({ isPinned: Boolean(args.isPinned) });
				break;
			case 'DeleteConversation':
				state.conversations.delete(id);
				state.working.delete(id);
				if (state.chat?.id === id) {
					closeChat();
				}
				break;
			case 'SaveDraft':
			case 'SetTaskCheckOff':
			case 'SaveBackendSession':
			case 'SetDirectory':
			case 'SetWorktree':
			case 'ReleaseWorktree':
				break;
			default:
				requestSync();
				break;
		}
	}
	renderListSoon();
	if (state.chat) {
		renderHeader();
	}
}

// ---------- Chats ----------

// A new chat's seq of -1 makes its first attach always carry the messages.
function createChat(id) {
	return { id, seq: -1, attempt: 0, messages: [], tools: new Map(), intents: {}, session: null, attached: false, buffer: null, missing: false, composer: '', lastSent: '', inputs: new Map() };
}

function openChat(id) {
	location.hash = 'chat=' + id;
}

function closeChat() {
	if (location.hash.startsWith('#chat=')) {
		// Replacing the entry keeps Back from leaving the page when the chat was opened from a notification.
		history.replaceState(null, '', location.pathname + location.search);
		route();
	}
}

function route() {
	const match = /^#chat=([0-9a-f]{32})$/i.exec(location.hash);
	const id = match ? match[1].toLowerCase() : null;
	if (state.chat && state.chat.id !== id) {
		leaveChat();
	}
	if (id && !state.chat) {
		state.chat = state.chats.get(id) ?? createChat(id);
		state.chats.delete(id);
		if (state.connected && !document.hidden) {
			attachChat(state.chat);
		}
	}
	render();
}

function leaveChat() {
	const chat = state.chat;
	state.chat = null;
	if (chat.attached && state.rpc) {
		state.rpc.request('detach', { ch: chat.id }).catch(() => {});
	}
	chat.attached = false;
	chat.buffer = null;
	state.chats.set(chat.id, chat);
	while (state.chats.size > CACHED_CHATS) {
		state.chats.delete(state.chats.keys().next().value);
	}
}

// Attaches the open chat: its pushes wait in a buffer until the answer and any further transcript pages are in. A newer attach or a detach
// meanwhile makes this one stale, so its answer changes nothing.
async function attachChat(chat) {
	const attempt = ++chat.attempt;
	chat.buffer = [];
	chat.attached = false;
	try {
		const rpc = state.rpc;
		const answer = await rpc.request('attach', { ch: chat.id, since: chat.seq });
		if (Array.isArray(answer.messages)) {
			for (let page = 1; page < (answer.pages ?? 1); page++) {
				// A newer attach or a detach drops the host's pages, so a stale attach stops asking for them.
				if (chat.attempt !== attempt) {
					return;
				}
				const more = await rpc.request('attach.page', { ch: chat.id, page });
				answer.messages.push(...(more.messages ?? []));
			}
		}
		if (chat.attempt !== attempt) {
			return;
		}
		if (state.chat !== chat) {
			state.rpc?.request('detach', { ch: chat.id }).catch(() => {});
			return;
		}
		if (Array.isArray(answer.messages)) {
			chat.messages = answer.messages;
		}
		chat.session = answer.state ?? null;
		chat.tools = new Map(Object.entries(answer.tools ?? {}));
		chat.intents = answer.intents ?? {};
		chat.seq = answer.seq ?? 0;
		chat.missing = false;
		const buffered = chat.buffer ?? [];
		chat.buffer = null;
		chat.attached = true;
		for (const frame of buffered) {
			if ((frame.seq ?? 0) > chat.seq) {
				applyChatPush(chat, frame);
			}
		}
		renderChat();
	} catch (error) {
		if (chat.attempt !== attempt) {
			return;
		}
		chat.buffer = null;
		if (state.chat !== chat) {
			return;
		}
		if (error.code === 'unknown-chat') {
			chat.missing = true;
			renderChat();
		} else if (error.code !== 'closed') {
			showBanner(error.message, true);
		}
	}
}

function applyChatPush(chat, frame) {
	if (typeof frame.seq === 'number') {
		if (frame.seq <= chat.seq) {
			return;
		}
		chat.seq = frame.seq;
	}
	switch (frame.t) {
		case 'state':
			chat.session = frame.s ?? null;
			renderSession();
			break;
		case 'msg':
			updateMessage(chat, frame.m);
			break;
		case 'msgs':
			chat.messages = Array.isArray(frame.m) ? frame.m : [];
			renderTranscript();
			break;
		case 'tool':
			if (frame.f == null) {
				chat.tools.delete(frame.id);
			} else {
				chat.tools.set(frame.id, frame.f);
			}
			refreshToolLine(chat, frame.id);
			break;
		case 'evt':
			applyEvent(chat, frame.e ?? {});
			break;
	}
}

function applyEvent(chat, event) {
	const args = Array.isArray(event.args) ? event.args : [];
	switch (event.name) {
		case 'PromptRefused':
			restoreComposer(chat, chat.lastSent);
			showBanner('Omp did not take the message.', true);
			break;
		case 'InterjectRefused':
		case 'UnqueueRestored':
		case 'EditorTextRequested':
			restoreComposer(chat, typeof args[0] === 'string' ? args[0] : '');
			break;
		case 'Cleared':
			chat.tools.clear();
			break;
	}
}

function callChat(chat, method, args = []) {
	if (!state.rpc) {
		return Promise.reject(new RemoteError('closed', 'Not connected to the host.'));
	}
	return state.rpc.request('call', { ch: chat.id, m: method, a: args }).then(answer => answer.r);
}

function reportFailure(error) {
	showBanner(error.message, true);
}

async function startChat(projectId, profile) {
	if (!state.rpc) {
		return;
	}
	const id = newGuid();
	const args = {
		id: toGuidD(id), createdAt: new Date().toISOString(), profile, projectId: projectId ? toGuidD(projectId) : null, title: 'New chat', hasChat: true,
	};
	try {
		await state.rpc.request('store', { op: { s: 'conversations', op: 'CreateConversation', a: args } });
		applyStoreOp({ s: 'conversations', op: 'CreateConversation', a: args });
		openChat(id);
	} catch (error) {
		reportFailure(error);
	}
}

// ---------- Notifications ----------

const canPush = () => 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window;

async function subscribePush() {
	const rpc = state.rpc;
	if (!rpc) {
		throw new RemoteError('closed', 'Not connected to the host.');
	}
	const registration = await navigator.serviceWorker.ready;
	const { key } = await rpc.request('push.key');
	const serverKey = decodeBase64Url(key);
	let subscription = await registration.pushManager.getSubscription();
	if (subscription && !sameBytes(subscription.options?.applicationServerKey, serverKey)) {
		await subscription.unsubscribe();
		subscription = null;
	}
	subscription ??= await registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: serverKey });
	const json = subscription.toJSON();
	await rpc.request('push.subscribe', { endpoint: json.endpoint, p256dh: json.keys.p256dh, auth: json.keys.auth });
	if (state.rpc !== rpc) {
		return;
	}
	state.pushOn = true;
	renderHeader();
}

async function enablePush() {
	try {
		if (await Notification.requestPermission() !== 'granted') {
			showBanner('Notifications are blocked for this page in the browser settings.', true);
			return;
		}
		await subscribePush();
		showBanner(null);
	} catch (error) {
		reportFailure(error);
	}
}

// Sends the subscription again on each connect, so the host has it even after it lost it.
function refreshPush() {
	if (canPush() && Notification.permission === 'granted') {
		subscribePush().catch(() => {});
	}
}

// ---------- Markdown ----------

const escapeHtml = text => String(text).replace(/[&<>"']/g, character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[character]);

// Links open only http and https pages; any other scheme (javascript:, data:, mailto:, file:) stays text.
const isSafeUrl = url => /^https?:\/\//i.test(url);

function formatText(text) {
	return escapeHtml(text)
		.replace(/\*\*(?=\S)([\s\S]*?\S)\*\*/g, '<strong>$1</strong>')
		.replace(/__(?=\S)([\s\S]*?\S)__/g, '<strong>$1</strong>')
		.replace(/(^|[^*\w])\*(?=\S)([^*]*?\S)\*(?![*\w])/g, '$1<em>$2</em>')
		.replace(/(^|[^_\w])_(?=\S)([^_]*?\S)_(?![_\w])/g, '$1<em>$2</em>')
		.replace(/~~(?=\S)([\s\S]*?\S)~~/g, '<del>$1</del>');
}

function renderLinks(text) {
	const pattern = /\[([^\]\n]+)\]\(\s*<?([^)\s>]+)>?(?:\s+"[^"]*")?\s*\)|(https?:\/\/[^\s<>()]*[^\s<>().,;:!?'"\]])/g;
	let html = '';
	let last = 0;
	for (const match of text.matchAll(pattern)) {
		html += formatText(text.slice(last, match.index));
		const url = match[2] ?? match[3];
		const label = match[1] !== undefined ? formatText(match[1]) : escapeHtml(url);
		html += isSafeUrl(url) ? `<a href="${escapeHtml(url)}" target="_blank" rel="noopener noreferrer">${label}</a>` : label;
		last = match.index + match[0].length;
	}
	return html + formatText(text.slice(last));
}

function renderInline(text) {
	let html = '';
	let last = 0;
	for (const match of text.matchAll(/(`+)([^`]|[^`][\s\S]*?[^`])\1(?!`)/g)) {
		html += renderLinks(text.slice(last, match.index));
		const code = /^ .* $/s.test(match[2]) ? match[2].slice(1, -1) : match[2];
		html += '<code>' + escapeHtml(code) + '</code>';
		last = match.index + match[0].length;
	}
	return html + renderLinks(text.slice(last));
}

const renderLines = lines => renderInline(lines.join('\n')).replace(/\n/g, '<br>');

const tableDivider = /^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?\s*$/;

function splitRow(line) {
	return line.trim().replace(/^\|/, '').replace(/\|$/, '').split('|').map(cell => cell.trim());
}

// A Markdown subset: paragraphs, headings, fenced code, lists, quotes, tables, rules, inline code, emphasis and links. Raw HTML stays text.
function renderMarkdown(source) {
	const lines = String(source).replace(/\r\n?/g, '\n').split('\n');
	const out = [];
	let paragraph = [];
	let list = null;
	const flushParagraph = () => {
		if (paragraph.length > 0) {
			out.push('<p>' + renderLines(paragraph) + '</p>');
			paragraph = [];
		}
	};
	const flushList = () => {
		if (list) {
			const tag = list.ordered ? 'ol' : 'ul';
			const start = list.ordered && list.start !== 1 ? ` start="${list.start}"` : '';
			out.push(`<${tag}${start}>` + list.items.map(item => '<li>' + renderLines(item) + '</li>').join('') + `</${tag}>`);
			list = null;
		}
	};
	const flush = () => {
		flushParagraph();
		flushList();
	};

	for (let index = 0; index < lines.length; index++) {
		const line = lines[index];
		const fence = /^\s{0,3}(`{3,}|~{3,})/.exec(line);
		if (fence) {
			flush();
			const body = [];
			index++;
			while (index < lines.length && !lines[index].trimStart().startsWith(fence[1])) {
				body.push(lines[index++]);
			}
			out.push('<pre><code>' + escapeHtml(body.join('\n')) + '</code></pre>');
			continue;
		}
		if (!line.trim()) {
			flush();
			continue;
		}
		const heading = /^\s{0,3}(#{1,6})\s+(.*?)(\s+#+)?\s*$/.exec(line);
		if (heading) {
			flush();
			out.push(`<h${heading[1].length}>${renderInline(heading[2])}</h${heading[1].length}>`);
			continue;
		}
		if (/^\s{0,3}([-*_])(\s*\1){2,}\s*$/.test(line)) {
			flush();
			out.push('<hr>');
			continue;
		}
		const item = /^\s*(?:[-*+]|(\d{1,9})[.)])\s+(.*)$/.exec(line);
		if (item) {
			flushParagraph();
			const ordered = item[1] !== undefined;
			if (!list || list.ordered !== ordered) {
				flushList();
				list = { ordered, start: ordered ? Number(item[1]) : 1, items: [] };
			}
			list.items.push([item[2]]);
			continue;
		}
		if (list && /^\s{2,}\S/.test(line)) {
			list.items.at(-1).push(line.trim());
			continue;
		}
		if (/^\s{0,3}>/.test(line)) {
			flush();
			const body = [];
			while (index < lines.length && /^\s{0,3}>/.test(lines[index])) {
				body.push(lines[index++].replace(/^\s{0,3}>\s?/, ''));
			}
			index--;
			out.push('<blockquote>' + renderMarkdown(body.join('\n')) + '</blockquote>');
			continue;
		}
		if (line.includes('|') && index + 1 < lines.length && tableDivider.test(lines[index + 1])) {
			flush();
			const head = splitRow(line);
			const rows = [];
			index += 2;
			while (index < lines.length && lines[index].includes('|') && lines[index].trim()) {
				rows.push(splitRow(lines[index++]));
			}
			index--;
			out.push('<table><thead><tr>' + head.map(cell => '<th>' + renderInline(cell) + '</th>').join('') + '</tr></thead><tbody>'
				+ rows.map(row => '<tr>' + head.map((_, column) => '<td>' + renderInline(row[column] ?? '') + '</td>').join('') + '</tr>').join('')
				+ '</tbody></table>');
			continue;
		}
		flushList();
		paragraph.push(line.trim());
	}
	flush();
	return out.join('');
}

// ---------- DOM helpers ----------

const icons = {
	back: '<svg viewBox="0 0 24 24"><path d="M15 18l-6-6 6-6"/></svg>',
	bell: '<svg viewBox="0 0 24 24"><path d="M6 8a6 6 0 0 1 12 0c0 7 3 9 3 9H3s3-2 3-9"/><path d="M10.3 21a1.94 1.94 0 0 0 3.4 0"/></svg>',
	plus: '<svg viewBox="0 0 24 24"><path d="M12 5v14M5 12h14"/></svg>',
	send: '<svg viewBox="0 0 24 24"><path d="M5 12h14M13 6l6 6-6 6"/></svg>',
	stop: '<svg viewBox="0 0 24 24"><rect x="6" y="6" width="12" height="12" rx="2"/></svg>',
};

function element(tag, attributes = {}, ...children) {
	const node = document.createElement(tag);
	for (const [name, value] of Object.entries(attributes)) {
		if (value === null || value === undefined || value === false) {
			continue;
		}
		if (name.startsWith('on')) {
			node.addEventListener(name.slice(2), value);
		} else if (name === 'html') {
			node.innerHTML = value;
		} else if (value === true) {
			node.setAttribute(name, '');
		} else {
			node.setAttribute(name, value);
		}
	}
	node.append(...children.flat().filter(child => child !== null && child !== undefined && child !== false));
	return node;
}

function markdownBlock(text) {
	return element('div', { class: 'markdown', html: renderMarkdown(text) });
}

const $ = id => document.getElementById(id);

function showBanner(text, isError = false) {
	const banner = $('banner');
	banner.hidden = !text;
	banner.textContent = text ?? '';
	banner.classList.toggle('error', Boolean(text) && isError);
}

function formatAge(value) {
	const time = Date.parse(value);
	if (Number.isNaN(time)) {
		return '';
	}
	const minutes = Math.floor((Date.now() - time) / 60000);
	if (minutes < 1) {
		return 'now';
	}
	if (minutes < 60) {
		return minutes + 'm';
	}
	if (minutes < 60 * 24) {
		return Math.floor(minutes / 60) + 'h';
	}
	if (minutes < 60 * 24 * 7) {
		return Math.floor(minutes / 1440) + 'd';
	}
	return new Date(time).toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
}

// ---------- Rendering ----------

const view = { kind: '', chatId: null, transcript: null, session: null, queue: null, dialog: null, dialogKey: '', composer: null, nodes: new Map() };

function render() {
	renderHeader();
	const screen = $('screen');
	if (!window.isSecureContext || !crypto.subtle) {
		return renderNotice('insecure', 'Loom needs a secure page',
			'The web client encrypts everything with WebCrypto, which browsers allow only over HTTPS or on localhost. Open it through tailscale serve, the relay, or funnel.');
	}
	if (state.pairing) {
		return renderPairing(screen);
	}
	if (!state.ready) {
		return renderNotice('loading', 'Loom', '');
	}
	if (!state.paired) {
		return renderNotice('unpaired', state.revoked ? 'This browser is not paired' : 'Pair this browser with Loom',
			(state.revoked ? 'The host says this device is not paired. Pair again to replace it. ' : '')
			+ 'On the PC running Loom, open Remote access and choose Connect a device. Scan the code with this device, or paste its link here.',
			renderLinkForm());
	}
	if (state.chat) {
		return renderChat();
	}
	return renderList();
}

function renderNotice(kind, title, text, ...extra) {
	view.kind = kind;
	view.chatId = null;
	$('screen').replaceChildren(element('div', { class: 'notice' }, element('h2', {}, title), text ? element('p', {}, text) : null, ...extra));
}

function renderHeader() {
	const chat = state.chat;
	const info = chat ? state.conversations.get(chat.id) : null;
	const project = info ? state.projects.find(item => toGuidN(item.id) === toGuidN(info.projectId)) : null;
	$('title').textContent = chat ? (info?.title ?? 'Chat') : 'Loom';
	$('subtitle').textContent = chat ? (project?.name ?? '') : (state.hostName ? state.hostName + (canControl() || !state.connected ? '' : ' · view only') : '');
	document.title = chat && info ? info.title + ' · Loom' : 'Loom';
	$('back').hidden = !chat;
	const listing = !chat && state.paired && !state.pairing;
	$('notify').hidden = !(listing && state.connected && canPush());
	$('notify').classList.toggle('on', state.pushOn);
	$('notify').setAttribute('aria-label', state.pushOn ? 'Notifications are on' : 'Turn on notifications');
	$('new-chat').hidden = !(listing && state.connected && canControl());
}

// The form that takes a pairing link pasted from Remote access: loom://pair#…, a web address with #pair=…, or the payload alone.
function renderLinkForm() {
	const input = element('input', { type: 'text', placeholder: 'loom://pair#…', autocomplete: 'off', autocapitalize: 'off', spellcheck: 'false', 'aria-label': 'Pairing link' });
	const button = element('button', { class: 'primary', type: 'submit' }, 'Continue');
	return element('form', {
		class: 'form',
		onsubmit: async event => {
			event.preventDefault();
			const text = input.value.trim();
			const marker = ['loom://pair#', '#pair='].map(prefix => [prefix, text.indexOf(prefix)]).find(([, index]) => index >= 0);
			const payload = marker ? text.slice(marker[1] + marker[0].length) : text;
			state.pairing = payload ? await readPairingLink(payload) : null;
			state.pairError = '';
			if (!state.pairing) {
				showBanner('That is not a Loom pairing link. Copy the link Remote access shows under the code.', true);
				return;
			}
			render();
		},
	}, element('label', {}, 'Pairing link', input), button);
}

function renderPairing(screen) {
	view.kind = 'pair';
	view.chatId = null;
	const name = element('input', { type: 'text', value: guessDeviceName(), maxlength: '100', 'aria-label': 'Device name' });
	const button = element('button', { class: 'primary', type: 'submit' }, 'Pair');
	const error = element('p', { class: 'error', role: 'alert' }, state.pairError);
	const form = element('form', {
		class: 'form',
		onsubmit: async event => {
			event.preventDefault();
			button.disabled = true;
			button.textContent = 'Pairing…';
			try {
				await pairDevice(state.pairing, name.value.trim() || guessDeviceName());
				state.pairing = null;
				state.pairError = '';
				state.paired = true;
				state.revoked = false;
				// The page talks to the host it just paired with from now on, whichever it showed before.
				dropHost();
				render();
				connect();
			} catch (failure) {
				state.pairError = failure.message;
				render();
			}
		},
	}, element('label', {}, 'Name this device', name), button);
	screen.replaceChildren(element('div', { class: 'notice' },
		element('h2', {}, 'Pair with Loom'),
		element('p', {}, 'This browser gets its own key and can then open the host\'s chats, with what the code allows: full control, or view only until you allow control in Remote access.'),
		form,
		state.pairError ? error : null));
}

function sortChats(items) {
	return items.sort((left, right) => {
		if (Boolean(left.isPinned) !== Boolean(right.isPinned)) {
			return left.isPinned ? -1 : 1;
		}
		const leftPlaced = typeof left.position === 'number';
		const rightPlaced = typeof right.position === 'number';
		if (leftPlaced !== rightPlaced) {
			return leftPlaced ? 1 : -1;
		}
		if (leftPlaced) {
			return left.position - right.position;
		}
		return Date.parse(right.createdAt || right.updatedAt) - Date.parse(left.createdAt || left.updatedAt);
	});
}

let listTimer = 0;

function renderListSoon() {
	if (view.kind !== 'list' || listTimer) {
		return;
	}
	listTimer = requestAnimationFrame(() => {
		listTimer = 0;
		renderList();
	});
}

function renderList() {
	if (state.chat || !state.paired || state.pairing) {
		return;
	}
	const screen = $('screen');
	const list = view.kind === 'list' ? screen.querySelector('.list') : null;
	const scroll = list?.scrollTop ?? 0;
	view.kind = 'list';
	view.chatId = null;

	const chats = [...state.conversations.values()].filter(info => info.hasChat !== false);
	const profiles = listProfiles();
	const profile = currentProfile();
	const control = canControl() && state.connected;
	const groups = [];
	const known = new Set();
	for (const project of state.projects.filter(item => item.profile === profile)) {
		const id = toGuidN(project.id);
		known.add(id);
		groups.push({ id, name: project.name, items: chats.filter(info => info.profile === profile && toGuidN(info.projectId) === id) });
	}
	groups.push({ id: null, name: 'Chats', items: chats.filter(info => info.profile === profile && !known.has(toGuidN(info.projectId))) });

	const body = element('div', { class: 'list' },
		profiles.length > 1 ? element('div', { class: 'profiles', role: 'tablist' }, profiles.map(item => element('button', {
			type: 'button', role: 'tab', class: item === profile ? 'on' : null, 'aria-selected': String(item === profile),
			onclick: () => {
				state.profile = item;
				localStorage.setItem(profileKey(state.hostId), item);
				renderList();
			},
		}, item))) : null,
		groups.filter(group => group.id !== null || group.items.length > 0 || groups.length === 1).map(group => element('section', { class: 'group' },
			element('div', { class: 'group-head' },
				element('span', {}, group.name),
				control ? element('button', { class: 'icon', type: 'button', 'aria-label': `New chat in ${group.name}`, html: icons.plus, onclick: () => startChat(group.id, profile) }) : null),
			sortChats(group.items).map(info => {
				const id = toGuidN(info.id);
				const working = state.working.has(id);
				return element('button', { class: 'chat', type: 'button', onclick: () => openChat(id) },
					element('span', { class: 'dot' + (working ? ' working' : info.isPinned ? ' pinned' : ''), title: working ? 'Working' : null }),
					element('span', { class: 'name' }, info.title || 'New chat'),
					element('span', { class: 'when' }, formatAge(info.updatedAt)));
			}))),
		renderBrowserTabs(),
		!state.connected && state.conversations.size === 0 ? element('p', { class: 'notice' }, 'Connecting to the host…') : null);
	screen.replaceChildren(body);
	body.scrollTop = scroll;
}

// The session's browser tabs, read-only: like a phone in Tern, this page has no browser, so the pages open in Loom on a computer.
function renderBrowserTabs() {
	const tabs = [...state.browserTabs.values()].sort((left, right) => (left.order ?? 0) - (right.order ?? 0));
	if (tabs.length === 0) {
		return null;
	}
	return element('section', { class: 'group browser-tabs', 'aria-label': 'Browser tabs' },
		element('div', { class: 'group-head' }, element('span', {}, 'Browser tabs')),
		tabs.map(tab => element('div', { class: 'chat browser-tab', title: tab.url },
			element('span', { class: 'dot' + (tab.loading ? ' working' : ''), title: tab.loading ? 'Loading' : null }),
			element('span', { class: 'name' }, tab.title || tab.url || 'New tab'),
			element('span', { class: 'when' }, formatTabHost(tab.url)))),
		element('p', { class: 'browser-note' }, 'Open on a computer: pages show in Loom\u2019s Browser panel there.'));
}

function formatTabHost(url) {
	try {
		return new URL(url).host || url;
	} catch {
		return url ?? '';
	}
}

function renderChat() {
	const chat = state.chat;
	if (!chat) {
		return;
	}
	renderHeader();
	const screen = $('screen');
	if (view.kind !== 'chat' || view.chatId !== chat.id) {
		view.kind = 'chat';
		view.chatId = chat.id;
		view.transcript = element('div', { class: 'transcript', role: 'log', 'aria-live': 'polite' });
		view.queue = element('div', { class: 'queue' });
		view.session = element('div', { class: 'state' });
		view.dialog = element('div', { hidden: true });
		view.dialogKey = '';
		view.composer = element('div');
		screen.replaceChildren(view.transcript, view.queue, view.session, view.dialog, view.composer);
		view.composerMode = '';
	}
	// The scopes are known once connected, and a view-only device gets a note instead of the composer.
	if (view.composerMode !== composerMode()) {
		buildComposer(chat);
	}
	renderTranscript();
	renderSession();
}

// The tool results by call id, so each tool call line shows its result and the result's own message is not shown apart.
function collectResults(messages) {
	const results = new Map();
	for (const message of messages) {
		const callId = message.role === 'toolResult' ? message.richContent?.toolCallId : null;
		if (callId) {
			results.set(callId, message);
		}
	}
	return results;
}

function collectCalls(messages) {
	const calls = new Set();
	for (const message of messages) {
		if (message.role === 'assistant' && Array.isArray(message.richContent?.content)) {
			for (const block of message.richContent.content) {
				if (block?.type === 'toolCall' && block.id) {
					calls.add(block.id);
				}
			}
		}
	}
	return calls;
}

function readContentText(content) {
	if (typeof content === 'string') {
		return content;
	}
	if (!Array.isArray(content)) {
		return '';
	}
	return content.map(block => block?.type === 'text' ? block.text ?? '' : block?.type === 'image' ? '[image]' : '').filter(Boolean).join('\n');
}

function isNearBottom(node) {
	return node.scrollHeight - node.scrollTop - node.clientHeight < 80;
}

function renderTranscript() {
	const chat = state.chat;
	if (!chat || view.kind !== 'chat') {
		return;
	}
	const transcript = view.transcript;
	const stick = isNearBottom(transcript) || transcript.childElementCount === 0;
	view.nodes = new Map();
	if (chat.missing) {
		transcript.replaceChildren(element('p', { class: 'notice' }, 'This chat no longer exists on the host.'));
		return;
	}
	const results = collectResults(chat.messages);
	const calls = collectCalls(chat.messages);
	transcript.replaceChildren(...chat.messages.map(message => {
		const node = buildMessage(chat, message, results, calls);
		view.nodes.set(message.id, node);
		return node;
	}));
	if (stick) {
		transcript.scrollTop = transcript.scrollHeight;
	}
}

function updateMessage(chat, message) {
	if (!message) {
		return;
	}
	const index = chat.messages.findIndex(item => item.id === message.id);
	if (index >= 0) {
		chat.messages[index] = message;
	} else {
		chat.messages.push(message);
	}
	if (view.kind !== 'chat' || view.chatId !== chat.id) {
		return;
	}
	const transcript = view.transcript;
	const stick = isNearBottom(transcript);
	const results = collectResults(chat.messages);
	const calls = collectCalls(chat.messages);
	const node = buildMessage(chat, message, results, calls);
	const old = view.nodes.get(message.id);
	if (old) {
		old.replaceWith(node);
	} else {
		transcript.append(node);
	}
	view.nodes.set(message.id, node);
	// A result shows in its call's line, so that line is built again.
	if (message.role === 'toolResult' && message.richContent?.toolCallId) {
		refreshToolLine(chat, message.richContent.toolCallId);
	}
	if (stick) {
		transcript.scrollTop = transcript.scrollHeight;
	}
}

function buildMessage(chat, message, results, calls) {
	const rich = message.richContent && typeof message.richContent === 'object' ? message.richContent : null;
	const node = element('div', { class: 'message ' + (message.role === 'user' ? 'user' : 'other') });
	switch (message.role) {
		case 'user': {
			const text = rich && 'content' in rich ? readContentText(rich.content) : message.text;
			node.append(element('div', { class: 'bubble' }, text || message.text || ''));
			break;
		}
		case 'assistant': {
			if (Array.isArray(rich?.content)) {
				for (const block of rich.content) {
					if (block?.type === 'text' && block.text?.trim()) {
						node.append(markdownBlock(block.text));
					} else if (block?.type === 'thinking' && block.thinking?.trim()) {
						node.append(buildFold('Thinking', '', () => [markdownBlock(block.thinking)]));
					} else if (block?.type === 'toolCall') {
						node.append(buildToolLine(chat, block, results.get(block.id)));
					} else if (block?.type === 'image') {
						node.append(element('div', { class: 'label' }, '[image]'));
					}
				}
			} else if (typeof rich?.content === 'string') {
				node.append(markdownBlock(rich.content));
			} else if (message.text) {
				node.append(markdownBlock(message.text));
			}
			if (rich?.stopReason === 'error') {
				node.append(element('div', { class: 'failure' }, rich.errorMessage || 'The model provider failed.'));
			}
			break;
		}
		case 'toolResult':
			// A result whose call is shown is part of the call's line.
			if (rich?.toolCallId && calls.has(rich.toolCallId)) {
				node.hidden = true;
			} else {
				node.append(buildToolLine(chat, { id: rich?.toolCallId ?? message.id, name: rich?.toolName ?? 'Tool result' }, message));
			}
			break;
		case 'custom':
			if (rich?.display === true) {
				node.append(element('div', { class: 'label' }, 'Notice'), markdownBlock(readContentText(rich.content) || message.text));
			} else {
				node.hidden = true;
			}
			break;
		case 'command':
			node.append(element('div', { class: 'label' }, rich?.command ?? 'Command'), markdownBlock(message.text));
			break;
		default:
			if (message.text) {
				node.append(element('div', { class: 'label' }, message.role), markdownBlock(message.text));
			} else {
				node.hidden = true;
			}
			break;
	}
	return node;
}

function buildFold(title, detail, buildBody, attributes = {}) {
	const fold = element('details', { class: 'fold', ...attributes },
		element('summary', {}, element('span', { class: 'tool-name' }, title), detail ? element('span', { class: 'tool-detail' }, detail) : null));
	// The body is built when opened, so long transcripts stay light.
	let built = false;
	fold.addEventListener('toggle', () => {
		if (fold.open && !built) {
			built = true;
			fold.append(...buildBody());
		}
	});
	return fold;
}

function summarizeArguments(args) {
	if (!args || typeof args !== 'object') {
		return '';
	}
	for (const name of ['command', 'path', 'file_path', 'pattern', 'query', 'url', 'description', 'prompt']) {
		if (typeof args[name] === 'string' && args[name].trim()) {
			return args[name].trim().split('\n')[0];
		}
	}
	const first = Object.values(args).find(value => typeof value === 'string' && value.trim());
	return first ? first.trim().split('\n')[0] : '';
}

function readToolStatus(chat, callId, result) {
	if (result) {
		return result.richContent?.isError ? 'failed' : 'done';
	}
	const live = chat.tools.get(callId);
	if (live) {
		return live.type === 'tool_execution_end' ? (live.isError ? 'failed' : 'done') : 'running';
	}
	return chat.session?.isBusy ? 'requested' : '';
}

function readToolOutput(chat, callId, result) {
	if (result) {
		return readContentText(result.richContent?.content) || result.text || '';
	}
	const live = chat.tools.get(callId);
	const output = live?.result ?? live?.partialResult ?? live?.update;
	if (!output) {
		return '';
	}
	return readContentText(output.content ?? output) || (typeof output === 'string' ? output : JSON.stringify(output, null, 2));
}

function buildToolLine(chat, call, result) {
	const callId = call.id ?? '';
	const status = readToolStatus(chat, callId, result);
	const intent = chat.intents?.[callId];
	const detail = typeof intent === 'string' && intent ? intent : summarizeArguments(call.arguments);
	const fold = buildFold(call.name ?? 'tool', detail, () => {
		const parts = [];
		if (call.arguments && Object.keys(call.arguments).length > 0) {
			parts.push(element('pre', {}, JSON.stringify(call.arguments, null, 2)));
		}
		const output = readToolOutput(chat, callId, result);
		if (output) {
			parts.push(element('pre', {}, output));
		}
		return parts.length > 0 ? parts : [element('div', { class: 'label' }, 'No output yet.')];
	}, { 'data-call': callId });
	if (status && status !== 'done') {
		fold.querySelector('summary').append(element('span', { class: 'tool-status ' + status }, status === 'failed' ? 'failed' : status === 'running' ? 'running' : 'requested'));
	}
	fold._call = call;
	fold._result = result;
	return fold;
}

function refreshToolLine(chat, callId) {
	if (view.kind !== 'chat' || view.chatId !== chat.id || !callId) {
		return;
	}
	const old = view.transcript.querySelector(`details[data-call="${CSS.escape(callId)}"]`);
	if (!old) {
		return;
	}
	const result = collectResults(chat.messages).get(callId) ?? old._result;
	const line = buildToolLine(chat, old._call, result);
	old.replaceWith(line);
	if (old.open) {
		line.open = true;
	}
}

function renderSession() {
	const chat = state.chat;
	if (!chat || view.kind !== 'chat') {
		return;
	}
	const session = chat.session ?? {};
	const parts = [];
	if (session.status) {
		parts.push(session.status);
	}
	if (session.connectedModel) {
		parts.push(session.connectedModel.split('/').at(-1));
	}
	if (session.contextWindow > 0 && typeof session.contextTokens === 'number') {
		parts.push(Math.round(session.contextTokens * 100 / session.contextWindow) + '% context');
	}
	view.session.replaceChildren(
		element('span', { class: 'dot' + (session.isBusy ? ' working' : '') }),
		element('span', { class: 'text' }, parts.join(' · ')));
	const queued = [
		...(session.queuedSteering ?? []).map(text => element('div', {}, 'Steering: ' + text)),
		...(session.queuedFollowUps ?? []).map(text => element('div', {}, 'Queued: ' + text)),
	];
	view.queue.replaceChildren(...queued);
	view.queue.hidden = queued.length === 0;
	renderDialog(chat);
	renderComposer();
}

// ---------- Dialogs ----------

function answerDialog(chat, id, response) {
	callChat(chat, 'AnswerDialog', [id, { type: 'extension_ui_response', id, ...response }]).catch(reportFailure);
}

function renderDialog(chat) {
	const dialogs = chat.session?.dialogs ?? [];
	const dialog = dialogs[0];
	const key = dialog ? JSON.stringify([dialog.id, chat.session?.selections ?? {}, canControl() && state.connected]) : '';
	if (key === view.dialogKey) {
		return;
	}
	view.dialogKey = key;
	if (!dialog) {
		view.dialog.hidden = true;
		view.dialog.replaceChildren();
		return;
	}
	const card = buildDialog(chat, dialog);
	view.dialog.replaceWith(card);
	view.dialog = card;
}

function buildDialog(chat, dialog) {
	const id = String(dialog.id ?? '');
	const method = dialog.method;
	const disabled = !(canControl() && state.connected);
	const card = element('div', { class: 'dialog', role: 'group', 'aria-label': 'Tool question' },
		element('div', { class: 'dialog-title' }, markdownBlock(dialog.title || 'Your input is needed')),
		dialog.message ? markdownBlock(dialog.message) : null);
	const actions = element('div', { class: 'actions' });
	const textOf = (inputKey, initial) => {
		if (!chat.inputs.has(inputKey)) {
			chat.inputs.set(inputKey, initial ?? '');
		}
		return chat.inputs.get(inputKey);
	};

	if (method === 'open_url') {
		const url = dialog.launchUrl || dialog.url || '';
		if (isSafeUrl(url)) {
			card.append(element('a', { href: url, target: '_blank', rel: 'noopener noreferrer' }, 'Open authorization page'));
		}
		if (dialog.instructions) {
			card.append(element('div', { class: 'hint' }, dialog.instructions));
		}
	} else if (method === 'select' && Array.isArray(dialog.options)) {
		const details = Array.isArray(dialog.optionDetails) ? dialog.optionDetails : [];
		dialog.options.forEach((option, index) => {
			const value = String(option ?? '');
			const description = details[index]?.description;
			card.append(element('button', { type: 'button', class: 'option', disabled, onclick: () => answerDialog(chat, id, { value }) },
				element('span', {}, value), description ? element('span', { class: 'description' }, description) : null));
		});
	} else if (method === 'confirm') {
		actions.append(
			element('button', { type: 'button', disabled, onclick: () => answerDialog(chat, id, { confirmed: false }) }, 'Decline'),
			element('button', { type: 'button', class: 'primary', disabled, onclick: () => answerDialog(chat, id, { confirmed: true }) }, 'Confirm'));
	} else if (method === 'ask' && Array.isArray(dialog.questions)) {
		const selections = chat.session?.selections ?? {};
		for (const question of dialog.questions) {
			const questionKey = id + '.' + question.id;
			const multi = question.multi === true || question.multi === 'true';
			const picked = new Set(selections[questionKey] ?? []);
			const custom = element('textarea', { rows: '2', disabled, 'aria-label': 'Custom answer' });
			custom.value = textOf(questionKey, '');
			custom.addEventListener('input', () => chat.inputs.set(questionKey, custom.value));
			card.append(element('div', { class: 'question' },
				markdownBlock(question.question ?? ''),
				(question.options ?? []).map(option => element('button', {
					type: 'button', class: 'option' + (picked.has(option.label) ? ' on' : ''), disabled, 'aria-pressed': String(picked.has(option.label)),
					onclick: () => callChat(chat, 'UpdateSelection', [questionKey, option.label, multi]).catch(reportFailure),
				}, element('span', {}, option.label), option.description ? element('span', { class: 'description' }, option.description) : null)),
				element('div', { class: 'hint' }, multi ? 'Or add your own answer' : 'Or write your own answer'),
				custom));
		}
		actions.append(element('button', {
			type: 'button', class: 'primary', disabled,
			onclick: () => {
				const answers = dialog.questions.map(question => {
					const questionKey = id + '.' + question.id;
					const customInput = (chat.inputs.get(questionKey) ?? '').trim();
					const multi = question.multi === true || question.multi === 'true';
					return { id: question.id, selectedOptions: !multi && customInput ? [] : [...(selections[questionKey] ?? [])], customInput };
				});
				answerDialog(chat, id, { answers });
			},
		}, 'Submit answers'));
	} else if (method === 'input' || method === 'editor') {
		const secret = dialog.secret === true || dialog.secret === 'true';
		const field = method === 'input'
			? element('input', { type: secret ? 'password' : 'text', placeholder: dialog.placeholder || null, disabled, 'aria-label': 'Tool answer' })
			: element('textarea', { rows: '5', disabled, 'aria-label': 'Tool answer' });
		field.value = textOf(id, dialog.prefill ?? dialog.value ?? '');
		field.addEventListener('input', () => chat.inputs.set(id, field.value));
		const submit = () => answerDialog(chat, id, { value: field.value });
		if (method === 'input') {
			field.addEventListener('keydown', event => {
				if (event.key === 'Enter') {
					event.preventDefault();
					submit();
				}
			});
		}
		card.append(field);
		actions.append(element('button', { type: 'button', class: 'primary', disabled, onclick: submit }, 'Submit answer'));
	}

	actions.prepend(method === 'open_url'
		? element('button', { type: 'button', class: 'quiet', disabled, onclick: () => callChat(chat, 'DismissDialog', [id]).catch(reportFailure) }, 'Dismiss')
		: element('button', { type: 'button', class: 'quiet', disabled, onclick: () => answerDialog(chat, id, { cancelled: true }) }, 'Cancel'));
	card.append(actions);
	return card;
}

// ---------- Composer ----------

const typesWithEnter = () => !window.matchMedia('(pointer: coarse)').matches;

const composerMode = () => state.scopes.length > 0 && !canControl() ? 'view' : 'edit';

function buildComposer(chat) {
	view.composerMode = composerMode();
	if (view.composerMode === 'view') {
		const note = element('div', { class: 'view-only' }, 'View only: this device reads chats. Allow control for it in Loom\'s Remote access panel to send messages.');
		view.composer.replaceWith(note);
		view.composer = note;
		return;
	}
	const input = element('textarea', { rows: '1', placeholder: 'Message', 'aria-label': 'Message' });
	input.value = chat.composer;
	const resize = () => {
		input.style.height = 'auto';
		input.style.height = input.scrollHeight + 2 + 'px';
	};
	input.addEventListener('input', () => {
		chat.composer = input.value;
		resize();
	});
	input.addEventListener('keydown', event => {
		if (event.key === 'Enter' && !event.shiftKey && !event.isComposing && typesWithEnter()) {
			event.preventDefault();
			submitComposer(chat);
		}
	});
	const steer = element('button', {
		type: 'button', class: 'steer', title: 'Steer the running turn instead of queueing after it',
		onclick: () => {
			state.steer = !state.steer;
			renderComposer();
		},
	}, 'Steer');
	const send = element('button', { type: 'button', class: 'icon send', 'aria-label': 'Send', html: icons.send, onclick: () => submitComposer(chat) });
	const stop = element('button', { type: 'button', class: 'icon stop', 'aria-label': 'Stop', html: icons.stop, onclick: () => callChat(chat, 'Stop').catch(reportFailure) });
	const composer = element('div', { class: 'composer' }, input, steer, stop, send);
	composer._parts = { input, steer, send, stop, resize };
	view.composer.replaceWith(composer);
	view.composer = composer;
	requestAnimationFrame(resize);
}

function renderComposer() {
	const parts = view.composer?._parts;
	if (!parts || view.kind !== 'chat') {
		return;
	}
	const busy = Boolean(state.chat?.session?.isBusy);
	parts.steer.hidden = !busy;
	parts.steer.classList.toggle('on', state.steer);
	parts.steer.setAttribute('aria-pressed', String(state.steer));
	parts.stop.hidden = !busy;
	parts.send.disabled = !state.connected;
	parts.send.setAttribute('aria-label', busy ? (state.steer ? 'Steer' : 'Queue') : 'Send');
}

function restoreComposer(chat, text) {
	if (!text) {
		return;
	}
	chat.composer = text;
	const parts = view.composer?._parts;
	if (parts && view.chatId === chat.id) {
		parts.input.value = text;
		parts.resize();
	}
}

async function submitComposer(chat) {
	const parts = view.composer?._parts;
	const text = parts?.input.value ?? '';
	if (!text.trim() || !state.rpc) {
		return;
	}
	const clear = () => {
		chat.composer = '';
		parts.input.value = '';
		parts.resize();
	};
	const busy = Boolean(chat.session?.isBusy);
	try {
		if (text.trim() === '/clear' && !busy) {
			clear();
			await callChat(chat, 'Clear');
		} else if (busy) {
			// While the agent works, the message queues for after the turn or steers it, as the Steer switch says.
			if (await callChat(chat, 'Interject', [text, [], state.steer]) !== false) {
				clear();
			} else {
				// The host's agent is still connecting; the message stays in the composer.
				showBanner('The agent is not ready for another message yet. Try again in a moment.', true);
			}
		} else {
			chat.lastSent = text;
			clear();
			await callChat(chat, 'Send', [text, []]);
		}
	} catch (error) {
		restoreComposer(chat, text);
		reportFailure(error);
	}
}

// ---------- Start ----------

async function start() {
	$('back').innerHTML = icons.back;
	$('notify').innerHTML = icons.bell;
	$('new-chat').innerHTML = icons.plus;
	$('back').addEventListener('click', closeChat);
	$('notify').addEventListener('click', enablePush);
	$('new-chat').addEventListener('click', () => startChat(null, currentProfile()));

	if (!window.isSecureContext || !crypto.subtle) {
		render();
		return;
	}

	if (location.hash.startsWith('#pair=')) {
		await beginPairing();
	}

	if ('serviceWorker' in navigator) {
		navigator.serviceWorker.register('sw.js').catch(() => {});
		navigator.serviceWorker.addEventListener('message', event => {
			if (typeof event.data?.open === 'string') {
				openChat(event.data.open);
			}
		});
	}

	// A pairing link opened in a tab that already shows the page changes only its fragment.
	window.addEventListener('hashchange', () => {
		if (location.hash.startsWith('#pair=')) {
			beginPairing().then(() => state.pairing ? render() : route(), reportFailure);
		} else {
			route();
		}
	});
	// A hidden page detaches its chat, so the host sends this device web pushes meanwhile; it attaches again when shown.
	document.addEventListener('visibilitychange', () => {
		const chat = state.chat;
		if (document.hidden) {
			if (chat && (chat.attached || chat.buffer) && state.rpc) {
				chat.attempt++;
				chat.attached = false;
				chat.buffer = null;
				state.rpc.request('detach', { ch: chat.id }).catch(() => {});
			}
			return;
		}
		if (!state.rpc) {
			state.retryDelay = 1000;
			connect();
		} else if (chat && state.connected && !chat.attached && !chat.buffer) {
			attachChat(chat);
		}
	});
	window.addEventListener('online', () => {
		if (!state.rpc) {
			state.retryDelay = 1000;
			connect();
		}
	});

	try {
		const host = await readHost();
		state.paired = Boolean(host);
		useHost(host?.hostId ?? null);
	} catch (error) {
		showBanner('This browser cannot keep Loom\'s key: ' + error.message, true);
	}
	state.ready = true;
	route();
	if (!state.pairing) {
		connect();
	}
}

// Reads the pairing link in the address's fragment and removes it, with its secret, from the address bar and the history at once.
async function beginPairing() {
	const payload = location.hash.slice('#pair='.length);
	history.replaceState(null, '', location.pathname + location.search);
	state.pairing = await readPairingLink(payload);
	state.pairError = '';
	if (!state.pairing) {
		showBanner('The pairing link is damaged. Show a new one on the host.', true);
	}
}

start();
