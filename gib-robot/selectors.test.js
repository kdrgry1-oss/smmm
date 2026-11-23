const { test } = require('node:test');
const assert = require('node:assert/strict');
const S = require('./selectors');
function pageWith(visible) {
  const make = value => ({
    first() { return this; },
    isVisible: async () => visible(String(value)),
    waitFor: async ({ timeout = 1000 } = {}) => {
      const start = Date.now();
      while (Date.now() - start <= timeout) {
        if (await visible(String(value))) return true;
        await new Promise(r => setTimeout(r, 10));
      }
      throw new Error('not visible');
    }
  });
  return { locator: make, getByLabel: make, getByPlaceholder: make, getByRole: make, getByText: make };
}
test('visible fallback is found without waiting for absent preferred selectors', async () => {
  const start = Date.now();
  assert.ok(await S.resolve(pageWith(v => v === 'form img'), 'captchaImage', { timeout: 5000 }));
  assert.ok(Date.now() - start < 500);
});
test('missing field uses one shared timeout for every candidate', async () => {
  const start = Date.now();
  assert.equal(await S.resolve(pageWith(() => false), 'loginUsername', { timeout: 100, optional: true }), null);
  assert.ok(Date.now() - start < 500);
});
test('delayed field is found within budget', async () => {
  const start = Date.now();
  assert.ok(await S.resolve(pageWith(v => v === 'input#userid' && Date.now() - start >= 50), 'loginUsername', { timeout: 600 }));
});
test('required missing field still reports a selector error', async () => {
  await assert.rejects(S.resolve(pageWith(() => false), 'loginUsername', { timeout: 0 }), S.ScreenChangedError);
});

test('current GIB dashboard greeting is recognized as a logged-in marker', async () => {
  const make = pattern => ({
    first() { return this; },
    isVisible: async () => pattern instanceof RegExp && pattern.test('Dijital Vergi Dairesine Hoş Geldiniz'),
    waitFor: async () => {
      if (!(pattern instanceof RegExp && pattern.test('Dijital Vergi Dairesine Hoş Geldiniz'))) throw new Error('not visible');
      return true;
    }
  });
  const page = { getByText: make, getByRole: () => make(''), locator: () => make('') };
  assert.ok(await S.resolve(page, 'loggedInMarker', { timeout: 0 }));
});
