import { canOperate } from './auth.js';
import { apiGet, apiPostEmpty } from './api.js';
import { initNav, requireAuth } from './nav.js';

if (!(await requireAuth())) throw new Error('unauthenticated');

initNav();

const grid = document.getElementById('serverGrid');
const refresh = document.getElementById('refreshBtn');
const lastEl = document.getElementById('lastUpdated');

let loading = false;

async function loadServers() {
  if (loading) return;
  loading = true;
  try {
    const resp = await apiGet('/api/servers');
    if (!resp) return;
    if (!resp.ok) { grid.innerHTML = `<p class="empty-state">Failed to load servers (${resp.status}).</p>`; return; }

    const servers = await resp.json();
    renderGrid(servers);
    lastEl.textContent = 'Updated ' + new Date().toLocaleTimeString();
  } catch {
    grid.innerHTML = '<p class="empty-state">Network error loading server list.</p>';
  } finally {
    loading = false;
  }
}

function renderGrid(servers) {
  if (!servers.length) {
    grid.innerHTML = '<p class="empty-state">No servers configured.</p>';
    return;
  }

  const op = canOperate();

  grid.innerHTML = servers.map(s => {
    const status = s.status ?? 'Unknown';
    const badgeClass = statusBadge(status);
    const cpu = s.cpuPercent != null ? s.cpuPercent.toFixed(1) + '%' : '-';
    const mem = s.memoryMb != null ? s.memoryMb.toFixed(0) + ' MB' : '-';
    const uptime = s.uptime ?? '-';

    const canStart = s.canStart === true;
    const canStop = s.canStop === true;
    const actionButtons = op ? `
      <button class="btn btn-secondary btn-sm" data-action="start"   data-id="${escHtml(s.id)}" ${!canStart ? 'disabled' : ''}>Start</button>
      <button class="btn btn-danger    btn-sm" data-action="stop"    data-id="${escHtml(s.id)}" ${!canStop ? 'disabled' : ''}>Stop</button>
      <button class="btn btn-ghost     btn-sm" data-action="restart" data-id="${escHtml(s.id)}" ${!(canStop || canStart) ? 'disabled' : ''}>Restart</button>
    ` : '';

    return `
      <div class="server-card">
        <div class="card-header">
          <div>
            <div class="card-name">${escHtml(s.name)}</div>
            <div class="card-module">${escHtml(s.module ?? '')}</div>
          </div>
          <span class="badge ${badgeClass}">${escHtml(status)}</span>
        </div>
        <div class="card-metrics">
          <div class="metric-item"><span class="metric-label">CPU</span><span class="metric-value">${cpu}</span></div>
          <div class="metric-item"><span class="metric-label">RAM</span><span class="metric-value">${mem}</span></div>
          <div class="metric-item"><span class="metric-label">Uptime</span><span class="metric-value">${escHtml(uptime)}</span></div>
        </div>
        <div class="card-actions">${actionButtons}</div>
        <div class="card-link">
          <a href="/servers/${pathSegment(s.id)}">Details &amp; Console -></a>
        </div>
      </div>
    `;
  }).join('');

  grid.querySelectorAll('[data-action]').forEach(btn => {
    btn.addEventListener('click', () => handleAction(btn.dataset.action, btn.dataset.id, btn));
  });
}

async function handleAction(action, id, btn) {
  btn.disabled = true;
  try {
    let resp;
    const serverPathId = pathSegment(id);
    if (action === 'start') resp = await apiPostEmpty(`/api/servers/${serverPathId}/start`);
    if (action === 'stop') resp = await apiPostEmpty(`/api/servers/${serverPathId}/stop`);
    if (action === 'restart') resp = await apiPostEmpty(`/api/servers/${serverPathId}/restart`);
    if (resp && !resp.ok) console.warn(`${action} returned ${resp.status}`);
    setTimeout(loadServers, 800);
  } finally {
    btn.disabled = false;
  }
}

function statusBadge(status) {
  const s = status.toLowerCase();
  if (s === 'online' || s === 'running') return 'badge-online';
  if (s.includes('warning') || s.includes('update')) return 'badge-warning';
  if (s === 'starting') return 'badge-starting';
  if (s === 'stopping') return 'badge-stopping';
  return 'badge-offline';
}

function escHtml(s) {
  return String(s ?? '').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
}

function pathSegment(s) {
  return encodeURIComponent(String(s ?? ''));
}

refresh.addEventListener('click', loadServers);

loadServers();
setInterval(loadServers, 10_000);
