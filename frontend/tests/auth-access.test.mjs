import assert from 'node:assert/strict';
import test from 'node:test';
import { loadModel } from './model-loader.mjs';
const { decideAccess, decideGuestAccess, safeReturnUrl, isAuthEndpoint, isStateChangingMethod } =
  await loadModel('auth');

const admin = { id: '1', username: 'root', role: 'admin' };
const viewer = { id: '2', username: 'lecteur', role: 'viewer' };

test('a fresh cluster sends everyone to the first administrator setup', () => {
  assert.deepEqual(decideAccess({ setupRequired: true, user: null }, false, '/flows'), {
    kind: 'redirect',
    path: '/setup',
  });
});

test('an anonymous visitor goes to login and returns where they were', () => {
  assert.deepEqual(decideAccess({ setupRequired: false, user: null }, false, '/flows?q=a'), {
    kind: 'redirect',
    path: '/login',
    returnUrl: '/flows?q=a',
  });
});

test('administrator pages are closed to viewers but open to administrators', () => {
  assert.deepEqual(decideAccess({ setupRequired: false, user: viewer }, true, '/settings'), {
    kind: 'redirect',
    path: '/',
  });
  assert.deepEqual(decideAccess({ setupRequired: false, user: admin }, true, '/settings'), {
    kind: 'allow',
  });
  assert.deepEqual(decideAccess({ setupRequired: false, user: viewer }, false, '/flows'), {
    kind: 'allow',
  });
});

test('login and setup pages only show when they are useful', () => {
  assert.equal(decideGuestAccess({ setupRequired: false, user: admin }, 'login').kind, 'redirect');
  assert.equal(decideGuestAccess({ setupRequired: true, user: null }, 'login').path, '/setup');
  assert.equal(decideGuestAccess({ setupRequired: false, user: null }, 'setup').path, '/login');
  assert.equal(decideGuestAccess({ setupRequired: false, user: null }, 'login').kind, 'allow');
  assert.equal(decideGuestAccess({ setupRequired: true, user: null }, 'setup').kind, 'allow');
});

test('the return address can never leave the application', () => {
  assert.equal(safeReturnUrl('/flows/1/edit'), '/flows/1/edit');
  assert.equal(safeReturnUrl('https://evil.example/'), '/');
  assert.equal(safeReturnUrl('//evil.example/'), '/');
  assert.equal(safeReturnUrl('/\\evil.example'), '/');
  assert.equal(safeReturnUrl('/login?returnUrl=%2Fflows'), '/');
  assert.equal(safeReturnUrl(null), '/');
});

test('authentication calls handle their own 401 and only writes carry the CSRF header', () => {
  assert.equal(isAuthEndpoint('/api/v1/auth/login'), true);
  assert.equal(isAuthEndpoint('/api/v1/flows'), false);
  assert.equal(isStateChangingMethod('post'), true);
  assert.equal(isStateChangingMethod('DELETE'), true);
  assert.equal(isStateChangingMethod('GET'), false);
});
