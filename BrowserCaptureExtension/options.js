const form = document.getElementById('pairingForm');
const input = document.getElementById('pairingCode');
const status = document.getElementById('status');
const forget = document.getElementById('forget');
const save = document.getElementById('save');
const reconnect = document.getElementById('reconnect');
const connection = document.getElementById('connection');
const connectionTitle = document.getElementById('connectionTitle');
const connectionDetail = document.getElementById('connectionDetail');
let checking = false;
let busy = false;
let paired = false;
let waitingForConnection = false;

function feedback(message, error = false) {
  status.textContent = message;
  status.dataset.error = String(error);
}

function setBusy(value) {
  busy = value;
  save.disabled = value;
  forget.disabled = value || !paired;
  reconnect.disabled = value;
}

async function showConnectionState() {
  if (checking) return;
  checking = true;
  try {
    const result = await chrome.runtime.sendMessage({ type: 'launcherPairingStatus' });
    if (!result || !['connected', 'connecting', 'unavailable', 'unpaired'].includes(result.state)) throw new Error();
    connection.dataset.state = result.state;
    paired = result.paired === true;
    const browser = result.browser === 'Edge' ? 'Edge' : 'Chrome';
    const views = {
      connected: [`${browser} connected to Launcher`, 'Pairing is confirmed for browser source capture, frontend links, and saved login filling.'],
      connecting: ['Connecting to Launcher…', 'Pairing is saved. Waiting for the launcher to confirm this browser.'],
      unavailable: ['Launcher connection unavailable', 'Keep FullStack Launcher open. If this continues, copy its pairing code again and save it here.'],
      unpaired: ['Pair this browser', 'Copy the pairing code from FullStack Launcher, then paste it below.']
    };
    [connectionTitle.textContent, connectionDetail.textContent] = views[result.state];
    reconnect.hidden = !result.paired || result.state === 'connected';
    forget.disabled = busy || !result.paired;
    if (waitingForConnection && result.state === 'connected') {
      waitingForConnection = false;
      feedback('Connection confirmed. Return to Launcher to continue.');
    }
  } catch {
    connection.dataset.state = 'unavailable';
    connectionTitle.textContent = 'Extension connection unavailable';
    connectionDetail.textContent = 'Reload this extension in the browser extensions page, then reopen setup.';
    reconnect.hidden = true;
  } finally { checking = false; }
}

form.addEventListener('submit', async event => {
  event.preventDefault();
  if (!event.isTrusted || busy) return;
  let pairingCode = input.value.trim().toLowerCase();
  if (!/^[a-f0-9]{64}$/.test(pairingCode)) {
    feedback('Pairing code must contain exactly 64 hexadecimal characters.', true);
    input.focus();
    return;
  }
  setBusy(true);
  let saved = false;
  try {
    await chrome.storage.local.set({ pairingCode });
    saved = true;
    pairingCode = '';
    input.value = '';
    waitingForConnection = true;
    feedback('Pairing saved. Checking the launcher connection…');
    // Retry also handles saving the same code, which may not raise a change.
    await chrome.runtime.sendMessage({ type: 'launcherPairingReconnect' });
    await showConnectionState();
  } catch {
    feedback(saved ? 'Pairing saved, but the connection check failed. Keep the launcher open and choose Try connection.'
      : 'Pairing could not be saved. Reload the extension and try again.', true);
  }
  finally { pairingCode = ''; setBusy(false); }
});

reconnect.addEventListener('click', async event => {
  if (!event.isTrusted || busy) return;
  setBusy(true);
  try {
    waitingForConnection = true;
    feedback('Checking the launcher connection…');
    await chrome.runtime.sendMessage({ type: 'launcherPairingReconnect' });
    await showConnectionState();
  } catch { feedback('Connection check failed. Keep FullStack Launcher open and try again.', true); }
  finally { setBusy(false); }
});

forget.addEventListener('click', async event => {
  if (!event.isTrusted || busy) return;
  setBusy(true);
  try {
    await chrome.storage.local.remove('pairingCode');
    waitingForConnection = false;
    input.value = '';
    feedback('Pairing removed from this browser.');
    await showConnectionState();
  } catch { feedback('Pairing could not be removed. Reload the extension and try again.', true); }
  finally { setBusy(false); }
});

document.addEventListener('visibilitychange', () => { if (!document.hidden) void showConnectionState(); });
setInterval(() => { if (!document.hidden && !busy) void showConnectionState(); }, 1500);
void showConnectionState();
