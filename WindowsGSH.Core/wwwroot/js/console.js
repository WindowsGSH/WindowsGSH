import { getToken, canOperate, refreshAccessToken, clearAuth } from './auth.js';
import { apiPost } from './api.js';
import { initNav, requireAuth } from './nav.js';

if (!(await requireAuth())) throw new Error('unauthenticated');

initNav();

// URL: /servers/<id>/console
const serverId = decodeURIComponent(window.location.pathname.split('/')[2] ?? '');
if (!serverId) window.location.href = '/dashboard';
const serverPathId = encodeURIComponent(serverId);

const outputEl  = document.getElementById('consoleOutput');
const filterEl  = document.getElementById('filterInput');
const pauseBtn  = document.getElementById('pauseBtn');
const clearBtn  = document.getElementById('clearBtn');
const copyBtn   = document.getElementById('copyBtn');
const inputRow  = document.getElementById('inputRow');
const inputEl   = document.getElementById('consoleInput');
const sendBtn   = document.getElementById('sendBtn');
const statusEl  = document.getElementById('wsStatus');
const titleEl   = document.getElementById('serverTitle');

if (titleEl) titleEl.textContent = `Console – ${serverId}`;
document.title = `Console – ${serverId} – WindowsGSH`;

// Show/hide input row based on role.
if (canOperate() && inputRow) inputRow.classList.remove('hidden');

let ws = null;
let paused = false;
let filterText = '';
const allLines = [];
const maxBufferedLines = 5000;
let reconnectTimer = null;

// ── WebSocket ─────────────────────────────────────────────────────────────────

async function connect() {
  clearTimeout(reconnectTimer);
  setStatus('connecting');

  if (!getToken() && !(await refreshAccessToken())) {
    clearAuth();
    window.location.href = '/';
    return;
  }

  if (ws) {
    ws.close();
    ws = null;
  }

  const token = getToken();
  if (!token) { window.location.href = '/'; return; }

  const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
  // Token is NOT placed in the URL — it would appear in server/proxy access logs and
  // the browser's URL history. Instead it is sent as the first WebSocket frame after
  // the connection opens ({"token":"..."}). The server expects this for browser clients.
  const url = `${proto}//${location.host}/api/servers/${serverPathId}/console/stream`;

  ws = new WebSocket(url);

  ws.addEventListener('open', () => {
    ws.send(JSON.stringify({ token }));
    setStatus('connected');
  });

  ws.addEventListener('message', (evt) => {
    let msg;
    try { msg = JSON.parse(evt.data); } catch { return; }

    if (msg.status === 'offline') {
      setStatus('offline');
      return;
    }

    if (msg.line != null) {
      allLines.push(msg);
      while (allLines.length > maxBufferedLines) allLines.shift();
      if (!paused) appendLine(msg);
    }
  });

  ws.addEventListener('close', () => {
    setStatus('disconnected');
    ws = null;
    reconnectTimer = setTimeout(async () => {
      if (await refreshAccessToken()) {
        connect();
      } else {
        clearAuth();
        window.location.href = '/';
      }
    }, 5000);
  });

  ws.addEventListener('error', () => {
    setStatus('error');
  });
}

function setStatus(s) {
  if (!statusEl) return;
  const labels = { connecting: '⏳ Connecting…', connected: '🟢 Connected', disconnected: '🔴 Disconnected (reconnecting…)', offline: '⚫ Server offline', error: '⚠ Error' };
  statusEl.textContent = labels[s] ?? s;
}

// ── Rendering ─────────────────────────────────────────────────────────────────

function appendLine(msg) {
  const text = msg.line ?? '';
  if (filterText && !text.toLowerCase().includes(filterText)) return;

  const span = document.createElement('span');
  span.className = 'log-line ' + lineClass(text);
  span.textContent = text;
  outputEl.appendChild(span);

  // Keep at most 5000 DOM lines to avoid memory issues.
  while (outputEl.children.length > 5000) outputEl.firstChild.remove();

  outputEl.scrollTop = outputEl.scrollHeight;
}

function rerender() {
  outputEl.innerHTML = '';
  const filtered = filterText
    ? allLines.filter(m => (m.line ?? '').toLowerCase().includes(filterText))
    : allLines;
  // Show at most the last 2000 lines after a filter change.
  filtered.slice(-2000).forEach(appendLine);
}

// ── Controls ──────────────────────────────────────────────────────────────────

filterEl?.addEventListener('input', () => {
  filterText = filterEl.value.toLowerCase().trim();
  rerender();
});

pauseBtn?.addEventListener('click', () => {
  paused = !paused;
  pauseBtn.textContent = paused ? '▶ Resume' : '⏸ Pause';
  if (!paused) {
    // Catch up with buffered lines.
    rerender();
    outputEl.scrollTop = outputEl.scrollHeight;
  }
});

clearBtn?.addEventListener('click', () => {
  outputEl.innerHTML = '';
  // Clear only the DOM view; the buffer (allLines) is kept for filter re-renders.
});

copyBtn?.addEventListener('click', async () => {
  const text = Array.from(outputEl.children).map(el => el.textContent).join('\n');
  try {
    await navigator.clipboard.writeText(text);
    copyBtn.textContent = '✓ Copied';
    setTimeout(() => { copyBtn.textContent = 'Copy'; }, 1500);
  } catch {
    copyBtn.textContent = 'Failed';
    setTimeout(() => { copyBtn.textContent = 'Copy'; }, 1500);
  }
});

// ── Console input (Operator+) ─────────────────────────────────────────────────

async function sendCommand() {
  const cmd = inputEl?.value?.trim();
  if (!cmd) return;
  inputEl.value = '';

  try {
    const resp = await apiPost(`/api/servers/${serverPathId}/console/input`, { command: cmd });
    if (resp && !resp.ok) console.warn('Console input returned', resp.status);
  } catch (e) {
    console.error('Console input error', e);
  }
}

sendBtn?.addEventListener('click', sendCommand);
inputEl?.addEventListener('keydown', (e) => { if (e.key === 'Enter') sendCommand(); });

// ── Start ─────────────────────────────────────────────────────────────────────

connect();

function lineClass(text) {
  const t = (text ?? '').toLowerCase();
  if (/\b(error|exception|fatal|critical)\b/.test(t)) return 'log-error';
  if (/\b(warn|warning)\b/.test(t)) return 'log-warn';
  return '';
}
