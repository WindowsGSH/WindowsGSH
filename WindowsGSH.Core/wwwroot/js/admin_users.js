import { getRole } from './auth.js';
import { apiGet, apiPost, apiPatch, apiDelete, apiPostEmpty } from './api.js';
import { initNav, requireAuth } from './nav.js';

if (!(await requireAuth())) throw new Error('unauthenticated');
if (getRole() !== 'Admin') { window.location.href = '/dashboard'; throw new Error('forbidden'); }

initNav();

const errorBannerEl = document.getElementById('errorBanner');
const newUserFormEl = document.getElementById('newUserForm');
const userListEl    = document.getElementById('userList');
const tokenModalEl  = document.getElementById('tokenModal');
const tokenValueEl  = document.getElementById('tokenValue');
const formErrorEl   = document.getElementById('formError');

document.getElementById('newUserBtn').addEventListener('click', () => {
  newUserFormEl.classList.toggle('hidden');
  formErrorEl.classList.add('hidden');
  document.getElementById('newUsername').value = '';
  document.getElementById('newPassword').value = '';
  document.getElementById('newRole').value = 'Viewer';
});

document.getElementById('cancelNewBtn').addEventListener('click', () => {
  newUserFormEl.classList.add('hidden');
});

document.getElementById('createBtn').addEventListener('click', createUser);
document.getElementById('closeTokenBtn').addEventListener('click', () => tokenModalEl.classList.add('hidden'));
document.getElementById('copyTokenBtn').addEventListener('click', () => {
  navigator.clipboard?.writeText(tokenValueEl.textContent ?? '');
});

async function loadUsers() {
  const resp = await apiGet('/api/admin/users');
  if (!resp || !resp.ok) { showBanner('Failed to load users.'); return; }
  const users = await resp.json();
  renderTable(users);
}

function renderTable(users) {
  if (!users.length) {
    userListEl.innerHTML = '<p class="empty-state">No users found.</p>';
    return;
  }

  userListEl.innerHTML = `
    <table class="admin-table">
      <thead>
        <tr>
          <th>Username</th>
          <th>Role</th>
          <th>Status</th>
          <th>Last login</th>
          <th></th>
        </tr>
      </thead>
      <tbody id="userRows"></tbody>
    </table>
  `;

  const tbody = document.getElementById('userRows');
  for (const u of users) {
    const tr = document.createElement('tr');
    tr.dataset.id = u.id;
    tr.innerHTML = `
      <td>${escHtml(u.username)}${u.forcePasswordChange ? ' <span class="badge badge-warning" title="Must change password">!</span>' : ''}</td>
      <td>
        <select class="input" data-field="role">
          ${['Viewer','Operator','Admin'].map(r => `<option${r === u.role ? ' selected' : ''}>${r}</option>`).join('')}
        </select>
      </td>
      <td>
        <label>
          <input type="checkbox" data-field="enabled" ${u.enabled ? 'checked' : ''}>
          ${u.enabled ? 'Enabled' : 'Disabled'}
        </label>
      </td>
      <td class="timestamp-muted">${u.lastLoginUtc ? new Date(u.lastLoginUtc).toLocaleString() : '—'}</td>
      <td class="col-actions">
        <button class="btn btn-ghost" data-action="save">Save</button>
        <button class="btn btn-ghost" data-action="reset">Reset pw</button>
        <button class="btn btn-danger" data-action="delete">Delete</button>
      </td>
    `;
    tbody.appendChild(tr);
  }

  tbody.addEventListener('click', handleTableAction);
}

async function handleTableAction(e) {
  const btn = e.target.closest('[data-action]');
  if (!btn) return;
  const tr = btn.closest('tr[data-id]');
  if (!tr) return;
  const id = parseInt(tr.dataset.id, 10);
  const action = btn.dataset.action;

  if (action === 'save') {
    const role = tr.querySelector('[data-field="role"]').value;
    const enabled = tr.querySelector('[data-field="enabled"]').checked;
    btn.disabled = true;
    const resp = await apiPatch(`/api/admin/users/${id}`, { role, enabled });
    btn.disabled = false;
    if (!resp || !resp.ok) {
      const err = await resp?.json().catch(() => ({}));
      showBanner(err?.detail ?? 'Save failed.');
    } else {
      await loadUsers();
    }
  } else if (action === 'reset') {
    if (!confirm('Generate a password reset token for this user?')) return;
    btn.disabled = true;
    const resp = await apiPostEmpty(`/api/admin/users/${id}/reset-password`);
    btn.disabled = false;
    if (!resp || !resp.ok) {
      const err = await resp?.json().catch(() => ({}));
      showBanner(err?.detail ?? 'Reset failed.');
    } else {
      const data = await resp.json();
      tokenValueEl.textContent = data.resetToken;
      tokenModalEl.classList.remove('hidden');
    }
  } else if (action === 'delete') {
    const username = tr.querySelector('td')?.textContent?.trim().replace(' !', '');
    if (!confirm(`Delete user '${username}'? This cannot be undone.`)) return;
    btn.disabled = true;
    const resp = await apiDelete(`/api/admin/users/${id}`);
    btn.disabled = false;
    if (!resp || !resp.ok) {
      const err = await resp?.json().catch(() => ({}));
      showBanner(err?.detail ?? 'Delete failed.');
    } else {
      await loadUsers();
    }
  }
}

async function createUser() {
  formErrorEl.classList.add('hidden');
  const username = document.getElementById('newUsername').value.trim();
  const password = document.getElementById('newPassword').value;
  const role     = document.getElementById('newRole').value;

  if (!username) { showFormError('Username is required.'); return; }
  if (password.length < 12) { showFormError('Password must be at least 12 characters.'); return; }

  const btn = document.getElementById('createBtn');
  btn.disabled = true;
  const resp = await apiPost('/api/admin/users', { username, password, role });
  btn.disabled = false;

  if (!resp || !resp.ok) {
    const err = await resp?.json().catch(() => ({}));
    showFormError(err?.detail ?? 'Failed to create user.');
  } else {
    newUserFormEl.classList.add('hidden');
    await loadUsers();
  }
}

function showBanner(msg) {
  errorBannerEl.textContent = msg;
  errorBannerEl.classList.remove('hidden');
  setTimeout(() => errorBannerEl.classList.add('hidden'), 5000);
}

function showFormError(msg) {
  formErrorEl.textContent = msg;
  formErrorEl.classList.remove('hidden');
}

function escHtml(s) {
  return String(s ?? '').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
}

loadUsers();
