import { setAuth, isAuthenticated, mustChangePassword, getUsername, getToken, clearAuth } from './auth.js';

// Already logged in - skip the login page unless a password change is required.
if (isAuthenticated() && !mustChangePassword()) window.location.href = '/dashboard';

const form = document.getElementById('loginForm');
const userEl = document.getElementById('username');
const passEl = document.getElementById('password');
const errorEl = document.getElementById('errorMsg');
const submitBtn = document.getElementById('submitBtn');
const loginHeaders = {
  'Content-Type': 'application/json',
  'X-WindowsGSH-Client': 'browser',
};
let changePasswordInitialized = false;

if (isAuthenticated() && mustChangePassword()) showChangePassword();

form.addEventListener('submit', async (e) => {
  e.preventDefault();
  errorEl.classList.add('hidden');
  submitBtn.disabled = true;
  submitBtn.textContent = 'Signing in...';

  try {
    const resp = await fetch('/api/auth/login', {
      method: 'POST',
      credentials: 'include',
      headers: loginHeaders,
      body: JSON.stringify({
        username: userEl.value.trim(),
        password: passEl.value,
      }),
    });

    if (!resp.ok) {
      const err = await resp.json().catch(() => ({}));
      showError(err.detail ?? err.title ?? 'Invalid username or password.');
      return;
    }

    const data = await resp.json();
    const username = userEl.value.trim();

    if (data.forcePasswordChange) {
      setAuth(data.accessToken, data.role, true, username);
      showChangePassword();
      return;
    }

    setAuth(data.accessToken, data.role, false, username);
    window.location.href = '/dashboard';
  } catch {
    showError('Could not reach the server. Check the web host is running.');
  } finally {
    submitBtn.disabled = false;
    submitBtn.textContent = 'Sign in';
  }
});

function showError(msg) {
  errorEl.textContent = msg;
  errorEl.classList.remove('hidden');
}

function showChangePassword() {
  form.classList.add('hidden');

  const panel = document.getElementById('changePasswordPanel');
  panel.classList.remove('hidden');

  const cpForm = document.getElementById('changePasswordForm');
  const cpError = document.getElementById('cpErrorMsg');
  const currentPass = document.getElementById('currentPasswordForChange');
  const newPass = document.getElementById('newPassword');
  const confPass = document.getElementById('confirmPassword');
  const cpBtn = document.getElementById('cpSubmitBtn');

  if (changePasswordInitialized) return;
  changePasswordInitialized = true;

  cpForm.addEventListener('submit', async (e) => {
    e.preventDefault();
    cpError.classList.add('hidden');

    if (newPass.value !== confPass.value) {
      cpError.textContent = 'Passwords do not match.';
      cpError.classList.remove('hidden');
      return;
    }

    cpBtn.disabled = true;
    cpBtn.textContent = 'Saving...';

    try {
      const resp = await fetch('/api/auth/change-password', {
        method: 'POST',
        credentials: 'include',
        headers: {
          'Content-Type': 'application/json',
          'Authorization': `Bearer ${getToken()}`,
        },
        body: JSON.stringify({
          currentPassword: currentPass.value || passEl.value,
          newPassword: newPass.value,
        }),
      });

      if (!resp.ok) {
        const err = await resp.json().catch(() => ({}));
        cpError.textContent = err.detail ?? err.title ?? 'Failed to change password.';
        cpError.classList.remove('hidden');
        return;
      }

      // Re-login with the new password to get fresh tokens.
      const loginResp = await fetch('/api/auth/login', {
        method: 'POST',
        credentials: 'include',
        headers: loginHeaders,
        body: JSON.stringify({ username: userEl.value.trim() || getUsername(), password: newPass.value }),
      });

      if (!loginResp.ok) {
        const err = await loginResp.json().catch(() => ({}));
        clearAuth();
        document.getElementById('changePasswordPanel').classList.add('hidden');
        form.classList.remove('hidden');
        showError(err.detail ?? err.title ?? 'Password changed, but sign-in failed. Sign in with the new password.');
        return;
      }

      const d = await loginResp.json();
      setAuth(d.accessToken, d.role, false, userEl.value.trim() || getUsername());
      window.location.href = '/dashboard';
    } catch {
      cpError.textContent = 'Network error. Try again.';
      cpError.classList.remove('hidden');
    } finally {
      cpBtn.disabled = false;
      cpBtn.textContent = 'Set password';
    }
  });
}
