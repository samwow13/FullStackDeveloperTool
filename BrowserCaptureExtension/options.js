const form = document.getElementById('pairingForm');
const input = document.getElementById('pairingCode');
const status = document.getElementById('status');
const forget = document.getElementById('forget');

async function showPairingState() {
  const saved = await chrome.storage.local.get('pairingCode');
  status.textContent = /^[a-f0-9]{64}$/.test(saved.pairingCode || '')
    ? 'Pairing saved. Keep this extension installed in the browser used for Snip.'
    : 'No pairing saved. Copy the code from Notes & queue.';
}

form.addEventListener('submit', async event => {
  event.preventDefault();
  const pairingCode = input.value.trim().toLowerCase();
  if (!/^[a-f0-9]{64}$/.test(pairingCode)) {
    status.textContent = 'Pairing code must contain exactly 64 hexadecimal characters.';
    return;
  }
  await chrome.storage.local.set({ pairingCode });
  input.value = '';
  await showPairingState();
});

forget.addEventListener('click', async () => {
  await chrome.storage.local.remove('pairingCode');
  input.value = '';
  await showPairingState();
});

showPairingState();
