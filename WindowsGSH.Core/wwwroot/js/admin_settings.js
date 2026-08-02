import { getRole } from './auth.js';
import { apiGet } from './api.js';
import { initNav, requireAuth } from './nav.js';

if (!(await requireAuth())) throw new Error('unauthenticated');
if (getRole() !== 'Admin') { window.location.href = '/dashboard'; throw new Error('forbidden'); }

initNav();

const contentEl = document.getElementById('settingsContent');

async function loadSettings() {
  const resp = await apiGet('/api/admin/settings');
  if (!resp || !resp.ok) {
    contentEl.innerHTML = '<p class="empty-state">Failed to load settings.</p>';
    return;
  }
  const s = await resp.json();
  render(s);
}

function render(s) {
  const statusBadge = s.isRunning
    ? '<span class="badge badge-online">Running</span>'
    : '<span class="badge badge-offline">Not running</span>';

  contentEl.innerHTML = `
    <div class="admin-settings-grid">
      <div class="admin-panel">
        <h3>Current web host</h3>
        <table class="admin-settings-table">
          <tr><td>Status</td><td>${statusBadge}</td></tr>
          <tr><td>Port</td><td>${escHtml(String(s.port ?? '—'))}</td></tr>
          <tr><td>Bind address</td><td>${escHtml(s.bindAddress ?? '—')}</td></tr>
        </table>
        <p class="admin-settings-note">
          To change the port or bind address, update the settings in the <strong>WindowsGSH desktop app</strong> and restart.
        </p>
      </div>

      <div class="admin-panel admin-tls-placeholder">
        <h3>HTTPS / TLS certificate</h3>
        <p class="admin-settings-note">HTTPS certificate configuration is not available in this version. Coming soon.</p>
      </div>
    </div>
  `;
}

function escHtml(s) {
  return String(s ?? '').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
}

loadSettings();
