// Renders the shared nav bar and wires up the logout button.

import { getRole, isAuthenticated, logout, mustChangePassword, refreshAccessToken } from './auth.js';

export function initNav() {
  const nav = document.getElementById('nav');
  if (!nav) return;

  const role = getRole() ?? '';
  // Only inject #serverTitle into the nav when it already lives inside the nav
  // (console.html). On server.html the <h2 id="serverTitle"> is in <main>, and
  // injecting a second copy into the nav would create a duplicate id, causing
  // document.getElementById('serverTitle') to return the nav span instead of the
  // page heading and leaving the visible server title stuck at "Loading…".
  const existingTitle = document.getElementById('serverTitle');
  const hasServerTitle = !!(existingTitle && nav.contains(existingTitle));
  const existingWsStatus = document.getElementById('wsStatus');
  const hasWsStatus = !!(existingWsStatus && nav.contains(existingWsStatus));

  nav.innerHTML = `
    <span class="nav-brand">Windows<span>GSH</span></span>
    <a href="/dashboard" class="nav-link-muted">Dashboard</a>
    ${hasServerTitle ? '<span id="serverTitle" class="nav-link-muted"></span>' : ''}
    ${role === 'Admin' ? '<a href="/admin/users" class="nav-link-muted">Admin</a>' : ''}
    <span class="nav-spacer"></span>
    ${hasWsStatus ? '<span id="wsStatus" class="timestamp-muted"></span>' : ''}
    <span class="nav-user">${escHtml(role)}</span>
    <button class="nav-logout" id="logoutBtn">Sign out</button>
  `;

  document.getElementById('logoutBtn').addEventListener('click', logout);
}

// Redirects to login if no access token can be restored.
export async function requireAuth() {
  if (isAuthenticated() && mustChangePassword()) {
    window.location.href = '/';
    return false;
  }

  if (!isAuthenticated() && !(await refreshAccessToken())) {
    window.location.href = '/';
    return false;
  }

  if (mustChangePassword()) {
    window.location.href = '/';
    return false;
  }

  return true;
}

function escHtml(s) {
  return String(s ?? '').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
}
