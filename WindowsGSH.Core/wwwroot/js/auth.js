// Token and role management. Access token is stored in sessionStorage only.
// The refresh token lives in an HttpOnly cookie managed by the server — JS never touches it.

const TOKEN_KEY = 'gsh_at';
const ROLE_KEY  = 'gsh_role';
const USERNAME_KEY = 'gsh_username';
const FORCE_PASSWORD_CHANGE_KEY = 'gsh_force_password_change';
const REFRESH_WAIT_TIMEOUT_MS = 12_000;

let refreshPromise = null;
const tabId = `${Date.now()}-${Math.random().toString(16).slice(2)}`;
const refreshChannel = typeof BroadcastChannel !== 'undefined'
  ? new BroadcastChannel('gsh_auth_refresh')
  : null;

export function getToken()  { return sessionStorage.getItem(TOKEN_KEY); }
export function getRole()   { return sessionStorage.getItem(ROLE_KEY); }
export function getUsername() { return sessionStorage.getItem(USERNAME_KEY); }
export function mustChangePassword() { return sessionStorage.getItem(FORCE_PASSWORD_CHANGE_KEY) === 'true'; }

export function setAuth(accessToken, role, forcePasswordChange = false, username = null) {
  const claims = parseJwtClaims(accessToken);
  sessionStorage.setItem(TOKEN_KEY, accessToken);
  sessionStorage.setItem(ROLE_KEY, role ?? claims?.role ?? '');
  sessionStorage.setItem(USERNAME_KEY, username ?? claims?.username ?? getUsername() ?? '');
  if (forcePasswordChange) {
    sessionStorage.setItem(FORCE_PASSWORD_CHANGE_KEY, 'true');
  } else {
    sessionStorage.removeItem(FORCE_PASSWORD_CHANGE_KEY);
  }
}

export function clearAuth() {
  sessionStorage.removeItem(TOKEN_KEY);
  sessionStorage.removeItem(ROLE_KEY);
  sessionStorage.removeItem(USERNAME_KEY);
  sessionStorage.removeItem(FORCE_PASSWORD_CHANGE_KEY);
}

export function isAuthenticated() { return !!getToken(); }

// Returns true for roles that can operate servers (start/stop/restart/console input).
export function canOperate() {
  const r = getRole();
  return r === 'Operator' || r === 'Admin';
}

export async function refreshAccessToken() {
  if (!refreshPromise) {
    refreshPromise = refreshAccessTokenCore().finally(() => {
      refreshPromise = null;
    });
  }

  return refreshPromise;
}

async function refreshAccessTokenCore() {
  if (navigator.locks?.request) {
    return navigator.locks.request('gsh-auth-refresh', { ifAvailable: true }, async (lock) => {
      if (!lock) return waitForRefreshResult();
      return performRefreshAsOwner();
    });
  }

  if (refreshChannel) {
    return coordinateRefreshWithChannel();
  }

  return performRefreshAsOwner();
}

function coordinateRefreshWithChannel() {
  const attemptId = `${Date.now()}-${tabId}-${Math.random().toString(16).slice(2)}`;
  const candidates = new Set([attemptId]);

  return new Promise((resolve) => {
    let resolved = false;
    let ownerStarted = false;
    let electTimer = null;
    let timeout = null;

    const finish = (ok, accessToken = null) => {
      if (resolved) return;
      resolved = true;
      cleanup();
      if (ok && accessToken) setAuth(accessToken, null, false);
      resolve(ok === true);
    };

    const onMessage = (event) => {
      const message = event.data;
      if (!message || message.sender === tabId) return;

      if (message.type === 'refresh-candidate' && message.attemptId) {
        candidates.add(message.attemptId);
        return;
      }

      if (message.type === 'refresh-owner') {
        ownerStarted = true;
        if (electTimer) clearTimeout(electTimer);
        return;
      }

      if (message.type === 'refresh-result') {
        finish(message.ok, message.accessToken);
      }
    };

    const cleanup = () => {
      refreshChannel.removeEventListener('message', onMessage);
      if (electTimer) clearTimeout(electTimer);
      if (timeout) clearTimeout(timeout);
    };

    refreshChannel.addEventListener('message', onMessage);
    refreshChannel.postMessage({ type: 'refresh-candidate', attemptId, sender: tabId });

    electTimer = setTimeout(async () => {
      if (ownerStarted) return;

      const owner = [...candidates].sort()[0];
      if (owner !== attemptId) return;

      ownerStarted = true;
      refreshChannel.postMessage({ type: 'refresh-owner', owner: attemptId, sender: tabId });
      const ok = await performRefreshAsOwner();
      finish(ok);
    }, 100);

    timeout = setTimeout(() => finish(false), REFRESH_WAIT_TIMEOUT_MS);
  });
}

async function performRefreshAsOwner() {
  try {
    const resp = await fetch('/api/auth/refresh', {
      method: 'POST',
      credentials: 'include',
      headers: { 'Content-Type': 'application/json' },
      body: '{}',
    });
    if (!resp.ok) {
      publishRefreshResult(false);
      return false;
    }

    const data = await resp.json();
    setAuth(data.accessToken, null, false);
    publishRefreshResult(true, data.accessToken);
    return true;
  } catch {
    publishRefreshResult(false);
    return false;
  }
}

function waitForRefreshResult() {
  return new Promise((resolve) => {
    let resolved = false;
    let timeout = null;
    const finish = (ok, accessToken = null) => {
      if (resolved) return;
      resolved = true;
      cleanup();
      if (ok && accessToken) setAuth(accessToken, null, false);
      resolve(ok === true);
    };

    const onMessage = (event) => {
      if (event.data?.type !== 'refresh-result') return;
      finish(event.data.ok, event.data.accessToken);
    };

    const cleanup = () => {
      refreshChannel?.removeEventListener('message', onMessage);
      if (timeout) clearTimeout(timeout);
    };

    refreshChannel?.addEventListener('message', onMessage);

    timeout = setTimeout(() => finish(false), REFRESH_WAIT_TIMEOUT_MS);
  });
}

function publishRefreshResult(ok, accessToken = null) {
  const result = { ok, accessToken, at: Date.now() };
  refreshChannel?.postMessage({ type: 'refresh-result', sender: tabId, ...result });
}

function parseJwtClaims(token) {
  try {
    const payload = token.split('.')[1];
    if (!payload) return null;
    const padded = payload.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(payload.length / 4) * 4, '=');
    return JSON.parse(atob(padded));
  } catch {
    return null;
  }
}

export async function logout() {
  // Require server confirmation before clearing local auth. If the server fails to
  // revoke the refresh token it will also fail to send the cookie-deletion header,
  // so clearing only the local access token would leave the session resumable via
  // the still-valid HttpOnly cookie.
  let resp;
  try {
    resp = await fetch('/api/auth/logout', {
      method: 'POST',
      credentials: 'include',
      headers: { 'Content-Type': 'application/json' },
      body: '{}',
    });
  } catch {
    alert('Sign out failed — could not reach the server. Close this browser tab to end your session, or try again.');
    return;
  }

  if (!resp.ok) {
    alert(`Sign out failed (server error ${resp.status}). Close this browser tab to end your session, or try again.`);
    return;
  }

  clearAuth();
  window.location.href = '/';
}
