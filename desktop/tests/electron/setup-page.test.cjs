const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const path = require('node:path');
const { runInNewContext } = require('node:vm');

const html = readFileSync(path.join(__dirname, '../../backend/OobManagement.Desktop.Host/Resources/setup.html'), 'utf8');
const scripts = [...html.matchAll(/<script\b[^>]*>([\s\S]*?)<\/script>/gi)];
assert.equal(scripts.length, 1, '必须执行实际交付页面的唯一脚本');
const flush = () => new Promise(resolve => setImmediate(resolve));
const ok = data => ({ ok: true, status: 200, json: async () => ({ success: true, data }) });
const failed = (errorCode, errorMessage = 'fixture server detail', status = 400) => ({
  ok: false, status, json: async () => ({ success: false, errorCode, errorMessage }),
});

/** 最小 DOM 只承接页面实际使用的节点与事件；不复制语言词典、初始化逻辑或浏览器业务行为。 */
async function setupPage({ savedLocale, hasDatabase = false, initialized = false, initialize = () => ok({ initialized: true }) } = {}) {
  const values = new Map([['fixture-preference', 'keep']]);
  if (savedLocale !== undefined) values.set('umi_locale', savedLocale);
  const elements = [...html.matchAll(/<([a-z][a-z0-9-]*)\b([^>]*)>/gi)].map(([, tag, attributes]) => {
    const attrs = new Map([...attributes.matchAll(/([a-z][a-z0-9-]*)\s*=\s*["']([^"']*)["']/gi)].map(([, key, value]) => [key, value]));
    const listeners = new Map();
    return {
      tagName: tag.toUpperCase(), id: attrs.get('id'), dataset: { i18n: attrs.get('data-i18n') },
      value: attrs.get('value') ?? '', textContent: '', required: /\brequired\b/.test(attributes),
      hidden: /\bhidden\b/.test(attributes), disabled: /\bdisabled\b/.test(attributes),
      autocomplete: attrs.get('autocomplete') ?? '', listeners,
      addEventListener(name, callback) {
        if (!listeners.has(name)) listeners.set(name, []);
        listeners.get(name).push(callback);
      },
    };
  });
  const byId = id => {
    const element = elements.find(candidate => candidate.id === id);
    assert.ok(element, `页面应包含 #${id}`);
    return element;
  };
  const document = {
    documentElement: { lang: 'zh-CN' }, title: '',
    querySelector(selector) {
      assert.match(selector, /^#[a-zA-Z]+$/);
      return byId(selector.slice(1));
    },
    querySelectorAll(selector) {
      assert.equal(selector, '[data-i18n]');
      return elements.filter(element => element.dataset.i18n !== undefined);
    },
  };
  const requests = [];
  const navigations = [];
  const writes = [];
  runInNewContext(scripts[0][1], {
    document,
    navigator: { language: 'en-US', languages: ['en-US'] },
    localStorage: {
      getItem: key => values.get(key) ?? null,
      setItem: (key, value) => { writes.push([key, value]); values.set(key, value); },
    },
    location: {
      replace: destination => navigations.push(destination),
      reload: () => assert.fail('语言切换不得刷新初始化页面'),
    },
    fetch: async (url, options) => {
      requests.push({ url, options });
      if (url === '/desktop/status') return ok({ initialized, hasDatabase });
      if (url === '/desktop/csrf') return ok({ token: 'fixture-csrf-token' });
      assert.equal(url, '/desktop/initialize', '页面只能调用正式本地设置接口');
      return initialize();
    },
  }, { timeout: 1000, filename: 'desktop-setup-page-fixture.js' });
  await flush();
  async function dispatch(id, name) {
    let prevented = false;
    const event = { target: byId(id), preventDefault() { prevented = true; } };
    for (const listener of byId(id).listeners.get(name) ?? []) await listener(event);
    if (name === 'submit') assert.equal(prevented, true);
    await flush();
  }
  return {
    byId, document, elements, values, writes, requests, navigations,
    label: key => elements.find(element => element.dataset.i18n === key).textContent,
    async change(locale) { byId('language').value = locale; await dispatch('language', 'change'); },
    submit: () => dispatch('setup', 'submit'),
    text: () => [document.title, ...elements.map(element => element.textContent)].join('\n'),
  };
}

test('英文系统语言下新初始化页默认简中，并只读取本地状态', async () => {
  const page = await setupPage();
  assert.equal(page.document.documentElement.lang, 'zh-CN');
  assert.equal(page.document.title, '带外管理 · 本地设置');
  assert.equal(page.label('password'), '管理员密码');
  assert.equal(page.values.get('umi_locale'), 'zh-CN');
  assert.equal(page.values.get('fixture-preference'), 'keep');
  assert.equal(page.byId('submit').disabled, false);
  assert.deepEqual(page.requests.map(request => request.url), ['/desktop/status']);
  assert.equal(page.requests[0].options, undefined);
  assert.deepEqual(page.navigations, []);
});

test('保留三语已有偏好，切换同步语言与存储且不请求、不清密码', async () => {
  const titles = { 'zh-CN': '带外管理 · 本地设置', 'zh-TW': '帶外管理 · 本機設定', 'en-US': 'Out-of-band management · Local setup' };
  for (const savedLocale of Object.keys(titles)) {
    const page = await setupPage({ savedLocale });
    assert.equal(page.document.documentElement.lang, savedLocale);
    assert.equal(page.document.title, titles[savedLocale]);
    page.byId('password').value = 'fixture-kept-password';
    page.byId('confirm').value = 'fixture-kept-confirmation';
    for (const locale of ['en-US', 'zh-TW', 'zh-CN']) {
      await page.change(locale);
      assert.equal(page.document.documentElement.lang, locale);
      assert.equal(page.document.title, titles[locale]);
      assert.equal(page.byId('language').value, locale);
      assert.equal(page.values.get('umi_locale'), locale);
      assert.equal(page.byId('password').value, 'fixture-kept-password');
      assert.equal(page.byId('confirm').value, 'fixture-kept-confirmation');
    }
    assert.deepEqual(page.requests.map(request => request.url), ['/desktop/status']);
    assert.deepEqual(page.navigations, []);
  }
});

test('无效语言偏好恢复简中；升级页三语切换保留原密码并隐藏确认输入', async () => {
  const page = await setupPage({ savedLocale: 'unsupported', hasDatabase: true });
  assert.equal(page.values.get('umi_locale'), 'zh-CN');
  page.byId('password').value = 'fixture-current-admin-password';
  for (const locale of ['en-US', 'zh-TW', 'zh-CN']) {
    await page.change(locale);
    assert.equal(page.byId('confirm').hidden, true);
    assert.equal(page.byId('confirm').required, false);
    assert.equal(page.byId('confirmLabel').hidden, true);
    assert.equal(page.byId('password').autocomplete, 'current-password');
    assert.equal(page.byId('password').value, 'fixture-current-admin-password');
  }
  assert.match(page.byId('intro').textContent, /密码不会被重设/);
  assert.equal(page.requests.length, 1);
});

test('确认不一致及密码长度验证使用当前语言，并在切换后翻译已有提示', async () => {
  const page = await setupPage();
  page.byId('password').value = 'fixture-password-one';
  page.byId('confirm').value = 'fixture-password-two';
  await page.submit();
  assert.equal(page.byId('message').textContent, '两次密码不一致');
  await page.change('en-US');
  assert.equal(page.byId('message').textContent, 'The passwords do not match');
  await page.change('zh-TW');
  assert.equal(page.byId('message').textContent, '兩次密碼不一致');
  const errors = {
    'zh-CN': '管理员密码须为8至128个字符，且不能全部为空白',
    'zh-TW': '管理員密碼須為8至128個字元，且不能全部為空白',
    'en-US': 'Use 8–128 characters, not only whitespace, for the administrator password',
  };
  for (const [locale, expected] of Object.entries(errors)) {
    await page.change(locale);
    for (const value of ['', '1234567', 'x'.repeat(129), ' '.repeat(8)]) {
      page.byId('password').value = value;
      page.byId('confirm').value = value;
      await page.submit();
      assert.equal(page.byId('message').textContent, expected);
    }
  }
  assert.deepEqual(page.requests.map(request => request.url), ['/desktop/status']);
  assert.deepEqual(page.navigations, []);
});

test('API错误按errorCode与当前语言显示，升级不检查隐藏确认框或泄露服务端原文', async () => {
  const messages = {
    'zh-CN': '请提供有效管理员密码以确认升级',
    'zh-TW': '請提供有效的管理員密碼以確認升級',
    'en-US': 'Enter a valid administrator password to confirm the upgrade',
  };
  for (const [locale, expected] of Object.entries(messages)) {
    const password = 'fixture-current-password';
    const page = await setupPage({ savedLocale: locale, hasDatabase: true, initialize: () => failed('UNAUTHORIZED', `raw server detail ${password}`, 401) });
    page.byId('password').value = password;
    page.byId('confirm').value = 'different-hidden-confirmation';
    await page.submit();
    assert.equal(page.byId('message').textContent, expected);
    assert.equal(page.byId('password').value, password);
    assert.equal(page.byId('submit').disabled, false);
    assert.doesNotMatch(page.text(), /raw server detail|fixture-current-password/);
    assert.equal(page.requests.filter(request => request.options?.method === 'POST').length, 1);
    assert.deepEqual(page.navigations, []);
  }
  for (const [code, expected] of [
    ['CSRF_INVALID', 'The request has expired. Reload this page.'],
    ['DESKTOP_STORAGE_FAILED', 'Local initialization or upgrade did not complete. Keep the data directory and check the disk and backups.'],
    ['UNRECOGNIZED_FIXTURE_ERROR', 'The operation did not complete. Try again later.'],
  ]) {
    const page = await setupPage({ savedLocale: 'en-US', initialize: () => failed(code, '不得显示此服务端原文') });
    page.byId('password').value = page.byId('confirm').value = 'fixture-valid-password';
    await page.submit();
    assert.equal(page.byId('message').textContent, expected);
    assert.doesNotMatch(page.text(), /不得显示此服务端原文/);
  }
});

test('初始化提交保留CSRF，处理期间切语种与重复提交不产生新请求，成功后清密码并进入根页', async () => {
  let complete;
  const pending = new Promise(resolve => { complete = resolve; });
  const page = await setupPage({ initialize: () => pending });
  const password = 'fixture-private-admin-password';
  page.byId('password').value = page.byId('confirm').value = password;
  const submission = page.submit();
  await flush();
  assert.equal(page.byId('submit').disabled, true);
  await page.change('en-US');
  assert.equal(page.byId('message').textContent, 'Preparing local data. Keep the application running…');
  assert.equal(page.byId('password').value, password);
  assert.equal(page.byId('confirm').value, password);
  await page.submit();
  assert.deepEqual(page.requests.map(request => request.url), ['/desktop/status', '/desktop/csrf', '/desktop/initialize']);
  const initialize = page.requests[2].options;
  assert.equal(initialize.method, 'POST');
  assert.equal(initialize.headers['Content-Type'], 'application/json');
  assert.equal(initialize.headers['X-CSRF-TOKEN'], 'fixture-csrf-token');
  assert.deepEqual(JSON.parse(initialize.body), { password });
  assert.doesNotMatch(page.requests.map(request => request.url).join('\n'), /fixture-private-admin-password/);
  assert.doesNotMatch(JSON.stringify([...page.values]), /fixture-private-admin-password/);
  assert.doesNotMatch(page.text(), /fixture-private-admin-password/);
  assert.deepEqual(page.navigations, []);
  complete(ok({ initialized: true }));
  await submission;
  assert.equal(page.byId('password').value, '');
  assert.equal(page.byId('confirm').value, '');
  assert.deepEqual(page.navigations, ['/']);
});

test('已初始化状态只导航至业务入口，不发送初始化请求', async () => {
  const page = await setupPage({ initialized: true });
  assert.deepEqual(page.requests.map(request => request.url), ['/desktop/status']);
  assert.deepEqual(page.navigations, ['/']);
});
