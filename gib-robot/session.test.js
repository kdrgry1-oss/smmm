const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const root = fs.mkdtempSync(path.join(os.tmpdir(), 'gib-session-test-'));
process.env.SESSION_DIR = root;
const session = require('./session');
process.on('exit', () => fs.rmSync(root, { recursive: true, force: true }));
const state = { cookies: [], origins: [], __sessionStorage: { token: 'private-bearer' } };
test('encrypted state survives restart and preserves sessionStorage', () => {
  session.saveState('company', state);
  const file = session.sessionPath('company');
  assert.equal(fs.readFileSync(file, 'utf8').includes('private-bearer'), false);
  assert.equal(fs.statSync(file).mode & 0o777, 0o600);
  delete require.cache[require.resolve('./session')];
  assert.deepEqual(require('./session').loadState('company'), state);
  assert.equal(fs.readdirSync(root).some(f => f.endsWith('.tmp')), false);
});
test('legacy state migrates and expiration preserves last successful time', () => {
  fs.writeFileSync(session.sessionPath('legacy'), JSON.stringify(state));
  assert.deepEqual(session.loadState('legacy'), state);
  session.saveState('legacy', state);
  assert.equal(JSON.parse(fs.readFileSync(session.sessionPath('legacy'))).version, 1);
  session.markExpired('legacy');
  assert.equal(session.loadState('legacy'), null);
  assert.ok(session.readRawByKey(session.sessionKey('legacy')).parsed.lastSeenAt);
});
test('tampered state reports storage error rather than requesting login', () => {
  session.saveState('tamper', state);
  const file = session.sessionPath('tamper');
  const envelope = JSON.parse(fs.readFileSync(file));
  envelope.tag = Buffer.alloc(16).toString('base64');
  fs.writeFileSync(file, JSON.stringify(envelope));
  assert.throws(() => session.loadState('tamper'), { code: 'SESSION_STORAGE_ERROR' });
});
test('failed atomic replacement preserves previous session and reports failure', () => {
  session.saveState('write-failure', state);
  const original = fs.renameSync;
  fs.renameSync = () => { throw new Error('simulated full disk'); };
  try { assert.throws(() => session.saveState('write-failure', { cookies: [] }), { code: 'SESSION_STORAGE_ERROR' }); }
  finally { fs.renameSync = original; }
  assert.deepEqual(session.loadState('write-failure'), state);
});
test('expired local TTL is distinct from successful persistence', () => {
  session.saveState('old', state);
  const old = new Date(Date.now() - 13 * 3600000);
  fs.utimesSync(session.sessionPath('old'), old, old);
  assert.equal(session.loadState('old'), null);
  session.saveState('old', state);
  assert.deepEqual(session.loadState('old'), state);
});

test('unknown encrypted format is not treated as a legacy session', () => {
  fs.writeFileSync(session.sessionPath('unknown'), JSON.stringify({ version: 2, data: 'ciphertext' }));
  assert.throws(() => session.loadState('unknown'), { code: 'SESSION_STORAGE_ERROR' });
});
