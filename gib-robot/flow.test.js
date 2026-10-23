const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const { createRequire } = require('node:module');
const localRequire = createRequire(__filename);
function harness() {
  const calls = [];
  const selectors = { ...localRequire('./selectors'), isPresent: async (_, name) => name === 'loggedInMarker' };
  const session = {
    withClientLock: async (_, fn) => fn(), loadState: () => ({ cookies: [] }),
    saveState: (id, value) => calls.push({ id, value }), markExpired: id => calls.push({ expired: id })
  };
  const context = { module: { exports: {} }, require: name => name === './session' ? session : name === './selectors' ? selectors : localRequire(name), process, console, setTimeout, clearTimeout, Buffer, URL };
  vm.createContext(context);
  vm.runInContext(fs.readFileSync(require.resolve('./gib'), 'utf8') + `
    module.exports.test = { runFlow, isLoggedIn, ensureApiResponse, loginComplete, pendingLogins };
    openContext = async () => ({ browser: {}, context: { newPage: async () => ({}), storageState: async () => ({ cookies: [{ name: 'renewed' }], origins: [] }) } });
    ensureSession = async () => ({ reusedSession: true });
    attachJsonCollector = () => [];
    captureSessionStorage = async () => ({ token: 'rotated-token' });
    captureDiagnostic = async () => null;
    closeQuietly = async () => {};
  `, context);
  return { api: context.module.exports.test, session, selectors, calls };
}
test('successful fetch persists rotated token before returning', async () => {
  const h = harness();
  const result = await h.api.runFlow('u', 'p', async () => ({ ok: true }), { sessionKey: 'client:one' });
  assert.equal(result.ok, true);
  assert.equal(h.calls[0].id, 'client:one');
  assert.equal(h.calls[0].value.__sessionStorage.token, 'rotated-token');
});
test('storage failure cannot become successful fetch or forced login', async () => {
  const h = harness();
  h.session.saveState = () => { throw new Error('storage unavailable'); };
  const result = await h.api.runFlow('u', 'p', async () => ({ ok: true }));
  assert.equal(result.ok, false);
  assert.equal(result.needsCaptchaLogin, undefined);
  assert.equal(h.calls.length, 0);
});
test('unknown page is not successful login', async () => {
  const h = harness(); h.selectors.isPresent = async () => false;
  await assert.rejects(h.api.isLoggedIn({}), { code: 'SESSION_UNCERTAIN' });
});
test('explicit login form is logged out; marker with token is logged in', async () => {
  const h = harness(); h.selectors.isPresent = async (_, name) => name === 'loginPassword';
  assert.equal(await h.api.isLoggedIn({}), false);
  h.selectors.isPresent = async (_, name) => name === 'loggedInMarker';
  assert.equal(await h.api.isLoggedIn({ evaluate: async () => 'token' }), true);
});
test('missing token does not destroy state; provider 401 requires login', () => {
  const h = harness();
  assert.throws(() => h.api.ensureApiResponse({ tokenMissing: true }, 'test'), { code: 'SESSION_UNCERTAIN' });
  assert.throws(() => h.api.ensureApiResponse({ status: 401 }, 'test'), error => error.needsCaptchaLogin === true);
});

test('pending login cannot be completed for another tenant or account', async () => {
  const h = harness();
  h.api.pendingLogins.set('pending-one', { storageKey: 'client:tenant-one:account-one' });
  const result = await h.api.loginComplete({ pendingId: 'pending-one', code: '123', sessionKey: 'client:tenant-two:account-two' });
  assert.equal(result.ok, false);
  assert.equal(h.calls.length, 0);
  assert.equal(h.api.pendingLogins.has('pending-one'), true);
});
