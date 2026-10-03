// Local companion for the launcher's Snip tool. Page source is never sent
// until the launcher proves knowledge of the user's pairing code.
const BROKER_URL = 'ws://127.0.0.1:47873/browser-capture';
const EXTENSION_ID = 'poppjfplkbpgbabkabcbhdaifmbfcijf';
const MAX_CAPTURE_CHARS = 200000;
const HEARTBEAT_MS = 20000;

let socket = null;
let reconnectTimer = null;
let heartbeatTimer = null;
let proofTimer = null;
let authenticated = false;
let captureBusy = false;
let connecting = false;
let challenge = '';
let pairingCode = '';
let siteBusy = false;
let siteRevision = 0;
const siteOperations = new Map();

const browserName = /Edg\//.test(navigator.userAgent) ? 'msedge' : 'chrome';

function bytesFromHex(value) {
  if (!/^[a-f0-9]{64}$/.test(value)) return null;
  return Uint8Array.from(value.match(/../g), pair => Number.parseInt(pair, 16));
}

function base64(bytes) {
  return btoa(String.fromCharCode(...bytes));
}

function bytesFromBase64(value) {
  if (typeof value !== 'string' || !/^[A-Za-z0-9+/]{43}=$/.test(value)) return null;
  try {
    const bytes = Uint8Array.from(atob(value), character => character.charCodeAt(0));
    return bytes.length === 32 ? bytes : null;
  } catch {
    return null;
  }
}

function closeSocket() {
  clearInterval(heartbeatTimer);
  clearTimeout(proofTimer);
  heartbeatTimer = null;
  proofTimer = null;
  authenticated = false;
  captureBusy = false;
  if (socket) {
    const old = socket;
    socket = null;
    old.close();
  }
}

function scheduleReconnect(delay = 2000) {
  if (reconnectTimer) return;
  reconnectTimer = setTimeout(() => {
    reconnectTimer = null;
    connect();
  }, delay);
}

async function connect() {
  if (chrome.runtime.id !== EXTENSION_ID) return;
  if (connecting) return;
  if (socket && (socket.readyState === WebSocket.OPEN || socket.readyState === WebSocket.CONNECTING)) return;
  connecting = true;
  try {
    const stored = await chrome.storage.local.get('pairingCode');
    pairingCode = typeof stored.pairingCode === 'string' ? stored.pairingCode : '';
    if (!bytesFromHex(pairingCode)) return;
    if (socket && (socket.readyState === WebSocket.OPEN || socket.readyState === WebSocket.CONNECTING)) return;

    const next = new WebSocket(BROKER_URL);
    socket = next;
    next.addEventListener('open', () => {
      if (socket !== next) return;
      challenge = base64(crypto.getRandomValues(new Uint8Array(32)));
      next.send(JSON.stringify({
        type: 'hello', version: 1, browser: browserName,
        extensionId: chrome.runtime.id, challenge, siteOpenVersion: 1
      }));
      proofTimer = setTimeout(() => {
        if (!authenticated && socket === next) next.close();
      }, 5000);
    });
    next.addEventListener('message', event => {
      if (socket !== next) return;
      void handleMessage(next, event.data);
    });
    next.addEventListener('close', () => {
      if (socket !== next) return;
      closeSocket();
      scheduleReconnect();
    });
    next.addEventListener('error', () => {
      if (socket === next) next.close();
    });
  } catch {
    scheduleReconnect();
  } finally {
    connecting = false;
  }
}

async function verifyServerProof(message) {
  if (message?.type !== 'proof' || message.challenge !== challenge ||
      !bytesFromHex(pairingCode) || !bytesFromBase64(message.serverNonce)) return false;
  const signature = bytesFromBase64(message.signature);
  if (!signature) return false;
  try {
    const key = await crypto.subtle.importKey('raw', bytesFromHex(pairingCode),
      { name: 'HMAC', hash: 'SHA-256' }, false, ['verify']);
    return await crypto.subtle.verify('HMAC', key, signature,
      new TextEncoder().encode(`server:${challenge}`));
  } catch {
    return false;
  }
}

async function signClientProof(serverNonce) {
  const key = await crypto.subtle.importKey('raw', bytesFromHex(pairingCode),
    { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
  return base64(new Uint8Array(await crypto.subtle.sign('HMAC', key,
    new TextEncoder().encode(`client:${serverNonce}`))));
}

async function handleMessage(connection, raw) {
  let message;
  try { message = JSON.parse(raw); } catch { connection.close(); return; }
  if (!authenticated) {
    if (!await verifyServerProof(message)) { connection.close(); return; }
    if (socket !== connection) return;
    clearTimeout(proofTimer);
    proofTimer = null;
    const signature = await signClientProof(message.serverNonce);
    if (socket !== connection || connection.readyState !== WebSocket.OPEN) return;
    connection.send(JSON.stringify({
      type: 'ready', signature
    }));
    authenticated = true;
    heartbeatTimer = setInterval(() => {
      if (socket === connection && connection.readyState === WebSocket.OPEN)
        connection.send(JSON.stringify({ type: 'ping' }));
    }, HEARTBEAT_MS);
    return;
  }
  if (message?.type === 'capture') {
    if (captureBusy || siteBusy) {
      sendResult(connection, message.requestId, 'unavailable', '', '', '', '', ['Another capture is still running.']);
      return;
    }
    captureBusy = true;
    try { await captureForRequest(connection, message); }
    finally { captureBusy = false; }
    return;
  }
  if (message?.type === 'cancelSiteOpen') {
    const operation = siteOperations.get(message.requestId);
    if (operation) {
      operation.canceled = true;
      if (Number.isInteger(operation.createdTabId))
        void chrome.tabs.remove(operation.createdTabId).catch(() => {});
    }
    return;
  }
  if (message?.type === 'siteInventory' || message?.type === 'ensureSiteOpen') {
    if (captureBusy || siteBusy) {
      sendSiteResult(connection, message.requestId, 'unavailable', 0, siteRevision);
      return;
    }
    siteBusy = true;
    try { await siteForRequest(connection, message); }
    finally { siteBusy = false; }
  }
}

function sendSiteResult(connection, requestId, status, windowCount, revision) {
  if (socket !== connection || !authenticated || connection.readyState !== WebSocket.OPEN) return;
  // No tab URLs, titles, HTML, or CSS leave the browser during this operation.
  connection.send(JSON.stringify({ type: 'siteResult', requestId, status, windowCount, revision }));
}

function localSite(value) {
  if (typeof value !== 'string' || value.length > 4096) return null;
  try {
    const url = new URL(value);
    if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password ||
        !['localhost', '127.0.0.1', '[::1]'].includes(url.hostname.toLowerCase())) return null;
    return { href: url.href, protocol: url.protocol,
      port: url.port || (url.protocol === 'https:' ? '443' : '80') };
  } catch { return null; }
}

function sameLocalSite(value, expected) {
  const site = localSite(value);
  return site != null && site.protocol === expected.protocol && site.port === expected.port;
}

async function readSiteInventory(site, excludedTabId = null) {
  const revision = siteRevision;
  const excludedOperation = ownedSiteOperation(excludedTabId);
  const excludedUpdates = excludedOperation?.tabUpdateRevision || 0;
  const windows = await chrome.windows.getAll({ windowTypes: ['normal', 'popup'] });
  const tabs = await chrome.tabs.query({});
  // The newly created tab may load normally during this post-create query.
  // Subtract only that owned tab's update events; every other tab/window event
  // still invalidates the inventory, including removal/replacement of this tab.
  const ownUpdateDifference = (excludedOperation?.tabUpdateRevision || 0) - excludedUpdates;
  if (siteRevision - revision !== ownUpdateDifference || windows.length > 1024 || tabs.length > 10000)
    return { status: 'unavailable', windowCount: 0, revision: siteRevision };
  let found = false;
  let unknown = false;
  for (const tab of tabs) {
    if (tab.id === excludedTabId) continue;
    const urls = [tab.url, tab.pendingUrl].filter(value => typeof value === 'string' && value.length > 0);
    if (!urls.length) unknown = true;
    for (const url of urls) {
      try { new URL(url); } catch { unknown = true; continue; }
      if (sameLocalSite(url, site)) found = true;
    }
  }
  return { status: found ? 'found' : unknown ? 'unavailable' : 'absent',
    windowCount: windows.length, revision: siteRevision };
}

function ownedSiteOperation(tabId) {
  if (!Number.isInteger(tabId)) return null;
  for (const operation of siteOperations.values())
    if (operation.createdTabId === tabId) return operation;
  return null;
}

async function siteForRequest(connection, request) {
  const requestId = typeof request.requestId === 'string' ? request.requestId : '';
  const site = localSite(request.url);
  const expired = () => !Number.isSafeInteger(request.notAfterUtc) || Date.now() >= request.notAfterUtc;
  if (!/^[a-zA-Z0-9-]{1,128}$/.test(requestId) || !site || expired() ||
      request.notAfterUtc > Date.now() + 10000 ||
      (request.type === 'ensureSiteOpen' && (!Number.isSafeInteger(request.expectedRevision) || request.expectedRevision < 0))) {
    sendSiteResult(connection, requestId, 'unavailable', 0, siteRevision);
    return;
  }
  const operation = { canceled: false, createdTabId: null, tabUpdateRevision: 0 };
  if (request.type === 'ensureSiteOpen') {
    siteOperations.set(requestId, operation);
    // Retain the owned tab briefly so cancellation arriving during tabs.create
    // can remove that exact tab. Never remove a preexisting tab.
    setTimeout(() => siteOperations.delete(requestId), 10000);
  }
  const unavailable = windowCount => sendSiteResult(connection, requestId, 'unavailable', windowCount, siteRevision);
  try {
    let inventory = await readSiteInventory(site);
    if (inventory.status === 'found') {
      sendSiteResult(connection, requestId, 'found', inventory.windowCount, inventory.revision);
      return;
    }
    if (inventory.status !== 'absent' || expired() || operation.canceled ||
        socket !== connection || !authenticated || connection.readyState !== WebSocket.OPEN) {
      unavailable(inventory.windowCount);
      return;
    }
    if (request.type === 'siteInventory') {
      sendSiteResult(connection, requestId, 'absent', inventory.windowCount, inventory.revision);
      return;
    }
    if (inventory.revision !== request.expectedRevision || inventory.windowCount < 1) {
      unavailable(inventory.windowCount);
      return;
    }
    // Query again immediately before creation. Concurrent launcher requests are
    // serialized by the broker and this worker; browser changes invalidate revision.
    inventory = await readSiteInventory(site);
    if (inventory.status === 'found') {
      sendSiteResult(connection, requestId, 'found', inventory.windowCount, inventory.revision);
      return;
    }
    if (inventory.status !== 'absent' || inventory.revision !== request.expectedRevision ||
        expired() || operation.canceled || socket !== connection || !authenticated ||
        connection.readyState !== WebSocket.OPEN) {
      unavailable(inventory.windowCount);
      return;
    }
    const tab = await chrome.tabs.create({ url: site.href, active: false });
    operation.createdTabId = tab.id;
    if (!Number.isInteger(tab.id)) { unavailable(inventory.windowCount); return; }
    if (operation.canceled || expired() || socket !== connection || !authenticated ||
        connection.readyState !== WebSocket.OPEN) {
      await chrome.tabs.remove(tab.id).catch(() => {});
      unavailable(inventory.windowCount);
      return;
    }
    // Cover a manual tab creation in this profile while tabs.create was awaiting
    // completion. Keep the preexisting tab and remove only this request's new tab.
    inventory = await readSiteInventory(site, tab.id);
    if (inventory.status !== 'absent' || operation.canceled || expired() ||
        socket !== connection || !authenticated || connection.readyState !== WebSocket.OPEN) {
      await chrome.tabs.remove(tab.id).catch(() => {});
      sendSiteResult(connection, requestId, inventory.status === 'found' ? 'found' : 'unavailable',
        inventory.windowCount, siteRevision);
      return;
    }
    await chrome.tabs.update(tab.id, { active: true });
    if (operation.canceled || expired() || socket !== connection || !authenticated ||
        connection.readyState !== WebSocket.OPEN) {
      await chrome.tabs.remove(tab.id).catch(() => {});
      unavailable(inventory.windowCount);
      return;
    }
    await chrome.windows.update(tab.windowId, { focused: true }).catch(() => {});
    if (operation.canceled || expired() || socket !== connection || !authenticated ||
        connection.readyState !== WebSocket.OPEN) {
      await chrome.tabs.remove(tab.id).catch(() => {});
      unavailable(inventory.windowCount);
      return;
    }
    sendSiteResult(connection, requestId, 'opened', inventory.windowCount, siteRevision);
  } catch {
    if (Number.isInteger(operation.createdTabId))
      await chrome.tabs.remove(operation.createdTabId).catch(() => {});
    unavailable(0);
  }
}

function changedSiteInventory() {
  siteRevision++;
  if (socket && authenticated && socket.readyState === WebSocket.OPEN)
    socket.send(JSON.stringify({ type: 'siteInventoryChanged', revision: siteRevision }));
}

function sendResult(connection, requestId, status, title, url, html, css, issues, windowBounds = null) {
  if (socket !== connection || !authenticated || connection.readyState !== WebSocket.OPEN) return;
  connection.send(JSON.stringify({
    type: 'captureResult', requestId, status, title, url, html, css, issues, windowBounds
  }));
}

async function captureForRequest(connection, request) {
  const requestId = typeof request.requestId === 'string' ? request.requestId : '';
  if (!/^[a-zA-Z0-9-]{1,128}$/.test(requestId) ||
      request.browser !== browserName ||
      typeof request.expectedTitle !== 'string' || !request.expectedTitle ||
      request.expectedTitle.length > 1024 ||
      (request.expectedUrl != null && (typeof request.expectedUrl !== 'string' || request.expectedUrl.length > 4096))) {
    sendResult(connection, requestId, 'unavailable', '', '', '', '', ['Invalid capture request.']);
    return;
  }
  const maxHtmlChars = Math.min(MAX_CAPTURE_CHARS, request.maxHtmlChars);
  const maxCssChars = Math.min(MAX_CAPTURE_CHARS, request.maxCssChars);
  if (!Number.isInteger(maxHtmlChars) || maxHtmlChars < 1 ||
      !Number.isInteger(maxCssChars) || maxCssChars < 1) {
    sendResult(connection, requestId, 'unavailable', '', '', '', '', ['Invalid capture limits.']);
    return;
  }

  try {
    const browserWindow = await chrome.windows.getLastFocused();
    const windowBounds = browserWindow &&
      [browserWindow.left, browserWindow.top, browserWindow.width, browserWindow.height].every(Number.isFinite)
      ? { left: browserWindow.left, top: browserWindow.top,
          width: browserWindow.width, height: browserWindow.height }
      : null;
    if (!browserWindow || browserWindow.id < 0 || browserWindow.type !== 'normal') {
      sendResult(connection, requestId, 'mismatch', '', '', '', '', ['Selected browser window is unavailable.'], windowBounds);
      return;
    }
    const tabs = await chrome.tabs.query({ active: true, windowId: browserWindow.id });
    if (tabs.length !== 1 || !Number.isInteger(tabs[0].id)) {
      sendResult(connection, requestId, 'mismatch', '', '', '', '', ['Active browser tab is ambiguous.'], windowBounds);
      return;
    }
    const tab = tabs[0];
    const title = tab.title || '';
    const url = tab.url || '';
    if (title !== request.expectedTitle ||
        (request.expectedUrl && new URL(url).href !== new URL(request.expectedUrl).href)) {
      sendResult(connection, requestId, 'mismatch', title, url, '', '', ['Active tab changed after selection.'], windowBounds);
      return;
    }
    if (!/^https?:\/\//i.test(url)) {
      sendResult(connection, requestId, 'unavailable', title, url, '', '', ['Browser does not allow page source access for this tab.'], windowBounds);
      return;
    }
    const result = await chrome.scripting.executeScript({
      target: { tabId: tab.id, allFrames: false },
      func: capturePage,
      args: [maxHtmlChars, maxCssChars]
    });
    if (result.length !== 1 || !result[0].result) {
      sendResult(connection, requestId, 'unavailable', title, url, '', '', ['Browser returned no page source.'], windowBounds);
      return;
    }
    const page = result[0].result;
    if (!page.html) {
      sendResult(connection, requestId, 'unavailable', title, url, '', '', page.issues, windowBounds);
      return;
    }
    const current = await chrome.tabs.get(tab.id);
    if (current.windowId !== browserWindow.id || !current.active ||
        current.title !== title || current.url !== url) {
      sendResult(connection, requestId, 'mismatch', '', '', '', '', ['Tab changed during page capture.'], windowBounds);
      return;
    }
    sendResult(connection, requestId, page.issues.length ? 'partial' : 'captured',
      title, url, page.html, page.css, page.issues, windowBounds);
  } catch {
    sendResult(connection, requestId, 'unavailable', '', '', '', '',
      ['Browser could not capture this tab. Check extension site access and retry.']);
  }
}

// Serialized into the selected tab by chrome.scripting.executeScript.
function capturePage(maxHtmlChars, maxCssChars) {
  const issues = [];
  const addIssue = issue => { if (issues.length < 12) issues.push(issue); };
  const original = document.documentElement;
  if (!original) return { html: '', css: '', issues: ['Page has no HTML document.'] };

  // Reject pathological DOMs before making another full copy in the renderer.
  const walker = document.createTreeWalker(original,
    NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT | NodeFilter.SHOW_COMMENT);
  let nodeCount = 0;
  let estimatedCharacters = 0;
  for (let node = walker.currentNode; node; node = walker.nextNode()) {
    if (++nodeCount > 100000) return {
      html: '', css: '', issues: ['Page DOM exceeds the 100,000-node capture limit.']
    };
    estimatedCharacters += node.nodeValue?.length || 0;
    if (node.nodeType === Node.ELEMENT_NODE) {
      for (const attribute of node.attributes)
        estimatedCharacters += attribute.name.length + attribute.value.length;
    }
    if (estimatedCharacters > 4000000) return {
      html: '', css: '', issues: ['Page DOM exceeds the 4,000,000-character capture limit.']
    };
  }

  // Clone so sensitive form state is removed without changing the live page.
  const copy = original.cloneNode(true);
  for (const node of copy.querySelectorAll('script')) node.remove();
  for (const node of copy.querySelectorAll('input')) {
    if (node.type === 'hidden') { node.remove(); continue; }
    node.removeAttribute('value');
  }
  for (const node of copy.querySelectorAll('textarea')) node.textContent = '';
  for (const node of copy.querySelectorAll('option')) node.removeAttribute('selected');
  for (const node of copy.querySelectorAll('[contenteditable]')) node.textContent = '';
  function removeSensitiveAttributes(node) {
    for (const attribute of [...node.attributes]) {
      if (/^on/i.test(attribute.name) || attribute.name === 'srcdoc' ||
          /(token|secret|password|csrf|nonce|session|api[-_]?key)/i.test(attribute.name))
        node.removeAttribute(attribute.name);
    }
  }
  removeSensitiveAttributes(copy);
  for (const node of copy.querySelectorAll('*')) removeSensitiveAttributes(node);
  let html = copy.outerHTML;
  if (html.length > maxHtmlChars) {
    html = html.slice(0, maxHtmlChars);
    addIssue(`HTML was truncated at ${maxHtmlChars} characters.`);
  }

  let css = '';
  let cssTruncated = false;
  const seen = new Set();
  function appendCss(part) {
    if (cssTruncated) return false;
    const next = css ? `\n${part}` : part;
    if (css.length + next.length > maxCssChars) {
      css += next.slice(0, maxCssChars - css.length);
      cssTruncated = true;
      return false;
    }
    css += next;
    return true;
  }
  function collectSheet(sheet, name) {
    if (!sheet || seen.has(sheet) || cssTruncated) return;
    seen.add(sheet);
    let rules;
    try { rules = sheet.cssRules; }
    catch {
      addIssue(`${name} CSS rules are inaccessible (often a cross-origin stylesheet).`);
      return;
    }
    if (!appendCss(`/* ${name} */`)) return;
    for (const rule of rules) {
      if (!appendCss(rule.cssText)) break;
      if (rule.styleSheet) collectSheet(rule.styleSheet, `${name} imported stylesheet`);
      if (cssTruncated) break;
    }
  }
  let number = 0;
  for (const sheet of document.styleSheets) collectSheet(sheet, `stylesheet ${++number}`);
  for (const sheet of document.adoptedStyleSheets || []) collectSheet(sheet, `adopted stylesheet ${++number}`);
  if (cssTruncated) {
    addIssue(`CSS was truncated at ${maxCssChars} characters.`);
  }
  if (document.querySelector('iframe, frame')) addIssue('Embedded frame contents are not included.');
  for (const node of document.querySelectorAll('*')) {
    if (node.shadowRoot) {
      addIssue('Shadow DOM contents are not included.');
      break;
    }
  }
  return { html, css, issues };
}

chrome.storage.onChanged.addListener((changes, area) => {
  if (area !== 'local' || !changes.pairingCode) return;
  closeSocket();
  scheduleReconnect(100);
});
chrome.tabs.onActivated.addListener(() => scheduleReconnect(100));
chrome.tabs.onCreated.addListener(changedSiteInventory);
chrome.tabs.onRemoved.addListener(changedSiteInventory);
chrome.tabs.onReplaced.addListener(changedSiteInventory);
chrome.tabs.onUpdated.addListener((tabId, changes) => {
  if (Object.hasOwn(changes, 'url') || Object.hasOwn(changes, 'pendingUrl') ||
      Object.hasOwn(changes, 'status')) {
    const operation = ownedSiteOperation(tabId);
    if (operation) operation.tabUpdateRevision++;
    changedSiteInventory();
  }
});
chrome.windows.onCreated.addListener(changedSiteInventory);
chrome.windows.onRemoved.addListener(changedSiteInventory);
chrome.windows.onFocusChanged.addListener(() => scheduleReconnect(100));
chrome.runtime.onStartup.addListener(() => connect());
chrome.runtime.onInstalled.addListener(() => connect());
chrome.runtime.onConnect.addListener(() => connect());
chrome.action.onClicked.addListener(() => chrome.runtime.openOptionsPage());
chrome.alarms.onAlarm.addListener(alarm => {
  if (alarm.name === 'broker-reconnect') connect();
});
chrome.alarms.create('broker-reconnect', { periodInMinutes: 1 });
connect();
