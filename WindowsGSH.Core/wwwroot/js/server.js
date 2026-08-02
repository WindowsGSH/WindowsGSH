import { canOperate } from './auth.js';
import { apiGet, apiPostEmpty } from './api.js';
import { initNav, requireAuth } from './nav.js';

if (!(await requireAuth())) throw new Error('unauthenticated');

initNav();

// Extract server ID from URL: /servers/<id>
const serverId = decodeURIComponent(window.location.pathname.split('/')[2] ?? '');
if (!serverId) window.location.href = '/dashboard';
const serverPathId = encodeURIComponent(serverId);

const titleEl   = document.getElementById('serverTitle');
const statsEl   = document.getElementById('statGrid');
const actionsEl = document.getElementById('actionRow');
const consoleEl = document.getElementById('consoleOutput');
const consoleLinkEl = document.getElementById('consoleLink');

async function loadDetail() {
  const [detailResp, logsResp] = await Promise.all([
    apiGet(`/api/servers/${serverPathId}`),
    apiGet(`/api/servers/${serverPathId}/logs?max=50`),
  ]);

  if (!detailResp || !detailResp.ok) {
    titleEl.textContent = 'Server not found';
    return;
  }

  const s = await detailResp.json();
  titleEl.textContent = s.name;
  document.title = `${s.name} – WindowsGSH`;

  if (consoleLinkEl) consoleLinkEl.href = `/servers/${serverPathId}/console`;

  renderStats(s);
  renderActions(s);

  if (logsResp && logsResp.ok) {
    const lines = await logsResp.json();
    renderLogs(lines);
  }
}

function renderStats(s) {
  const status  = s.status ?? 'Unknown';
  const cpu     = s.cpuPercent != null ? s.cpuPercent.toFixed(1) + '%' : '—';
  const mem     = s.memoryMb  != null ? s.memoryMb.toFixed(0) + ' MB' : '—';
  const players = s.playerCount ?? '—';
  const ver     = s.version ?? '—';
  const port    = s.port ?? '—';

  statsEl.innerHTML = `
    <div class="stat-card">
      <div class="stat-label">Status</div>
      <div class="stat-value"><span class="badge ${statusBadge(status)}">${escHtml(status)}</span></div>
    </div>
    <div class="stat-card">
      <div class="stat-label">CPU</div>
      <div class="stat-value">${escHtml(cpu)}</div>
    </div>
    <div class="stat-card">
      <div class="stat-label">Memory</div>
      <div class="stat-value">${escHtml(mem)}</div>
    </div>
    <div class="stat-card">
      <div class="stat-label">Players</div>
      <div class="stat-value">${escHtml(players)}</div>
    </div>
    <div class="stat-card">
      <div class="stat-label">Uptime</div>
      <div class="stat-value">${escHtml(s.uptime ?? '—')}</div>
    </div>
    <div class="stat-card">
      <div class="stat-label">Port</div>
      <div class="stat-value">${escHtml(port)}</div>
    </div>
    <div class="stat-card">
      <div class="stat-label">Module</div>
      <div class="stat-value">${escHtml(s.module ?? '—')}</div>
    </div>
    <div class="stat-card">
      <div class="stat-label">Version</div>
      <div class="stat-value">${escHtml(ver)}</div>
    </div>
    ${s.installPath && canOperate() ? `<div class="stat-card stat-card-wide"><div class="stat-label">Install path</div><div class="stat-value stat-path">${escHtml(s.installPath)}</div></div>` : ''}
  `;
}

function renderActions(s) {
  if (!canOperate()) { actionsEl.innerHTML = ''; return; }

  const canStart = s.canStart === true;
  const canStop = s.canStop === true;
  actionsEl.innerHTML = `
    <button class="btn btn-secondary" id="startBtn" ${!canStart ? 'disabled' : ''}>Start</button>
    <button class="btn btn-danger"    id="stopBtn"  ${!canStop ? 'disabled' : ''}>Stop</button>
    <button class="btn btn-ghost"     id="rstBtn"   ${!(canStop || canStart) ? 'disabled' : ''}>Restart</button>
  `;

  document.getElementById('startBtn').addEventListener('click', () => doAction('start'));
  document.getElementById('stopBtn') .addEventListener('click', () => doAction('stop'));
  document.getElementById('rstBtn')  .addEventListener('click', () => doAction('restart'));
}

async function doAction(action) {
  const btn = document.getElementById(`${action === 'restart' ? 'rst' : action}Btn`);
  if (btn) btn.disabled = true;
  try {
    await apiPostEmpty(`/api/servers/${serverPathId}/${action}`);
    setTimeout(loadDetail, 1000);
  } finally {
    if (btn) btn.disabled = false;
  }
}

function renderLogs(lines) {
  if (!lines.length) { consoleEl.textContent = '(no recent output)'; return; }
  consoleEl.innerHTML = lines.map(l => {
    const cls = lineClass(l.line);
    return `<span class="log-line ${cls}">${escHtml(l.line)}</span>`;
  }).join('');
  consoleEl.scrollTop = consoleEl.scrollHeight;
}

function statusBadge(status) {
  const s = (status ?? '').toLowerCase();
  if (s === 'online' || s === 'running') return 'badge-online';
  if (s.includes('warning') || s.includes('update')) return 'badge-warning';
  if (s === 'starting') return 'badge-starting';
  if (s === 'stopping') return 'badge-stopping';
  return 'badge-offline';
}

function lineClass(text) {
  const t = (text ?? '').toLowerCase();
  if (/\b(error|exception|fatal|critical)\b/.test(t)) return 'log-error';
  if (/\b(warn|warning)\b/.test(t)) return 'log-warn';
  return '';
}

function escHtml(s) {
  return String(s ?? '').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
}

loadDetail();
