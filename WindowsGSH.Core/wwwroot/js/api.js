// Thin API client. Automatically attempts one token refresh on 401.
// Redirects to login on auth failure.

import { getToken, clearAuth, refreshAccessToken } from './auth.js';

async function request(url, options = {}) {
  const token = getToken();
  if (!token) { window.location.href = '/'; return null; }

  options.headers = {
    ...options.headers,
    'Authorization': `Bearer ${token}`,
  };
  options.credentials = 'include';

  let resp = await fetch(url, options);

  if (resp.status === 401) {
    if (await refreshAccessToken()) {
      options.headers['Authorization'] = `Bearer ${getToken()}`;
      resp = await fetch(url, options);
    } else {
      clearAuth();
      window.location.href = '/';
      return null;
    }
  }
  return resp;
}

export async function apiGet(url) {
  return request(url);
}

export async function apiPost(url, body) {
  return request(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
}

export async function apiPostEmpty(url) {
  return request(url, { method: 'POST' });
}

export async function apiPatch(url, body) {
  return request(url, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
}

export async function apiDelete(url) {
  return request(url, { method: 'DELETE' });
}
