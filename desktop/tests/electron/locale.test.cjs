const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const path = require('node:path');
const { runInNewContext } = require('node:vm');

const preload = readFileSync(path.join(__dirname, '../../dist/preload/index.js'), 'utf8');

/** 执行实际编译的沙箱预加载脚本，不启动 Electron 或访问运行数据。 */
function runPreload(values, { url = 'http://127.0.0.1:43123', storageError = false } = {}) {
  const writes = [];
  const exposed = new Map();
  const pageWindow = { addEventListener() {} };
  Object.defineProperty(pageWindow, 'localStorage', { get() {
    if (storageError) throw new Error('fixture storage unavailable');
    return {
      getItem: key => values.get(key) ?? null,
      setItem: (key, value) => { writes.push([key, value]); values.set(key, value); },
    };
  } });
  runInNewContext(preload, {
    exports: {},
    require: name => {
      // sandbox:true 的预加载只使用 Electron 提供的入口，不加载本地辅助模块。
      assert.equal(name, 'electron');
      return {
        contextBridge: { exposeInMainWorld: (key, value) => {
          exposed.set(key, { value, localeBeforePage: values.get('umi_locale') });
        } },
        ipcRenderer: { invoke() {}, send() {} },
      };
    },
    process: { versions: { electron: 'fixture' } },
    location: new URL(url),
    navigator: { language: 'en-US', languages: ['en-US'] },
    window: pageWindow,
    document: { addEventListener() {} },
  }, { timeout: 1000, filename: 'desktop-preload-fixture.js' });
  return { writes, exposed };
}

test('桌面新来源在页面脚本前默认简中，不跟随英文系统语言', () => {
  const values = new Map();
  const result = runPreload(values);
  assert.equal(values.get('umi_locale'), 'zh-CN');
  assert.deepEqual(result.writes, [['umi_locale', 'zh-CN']]);
  assert.equal(result.exposed.get('oobDesktop').localeBeforePage, 'zh-CN');
});

test('初始化页切换语言后，同来源登录页与刷新保留用户选择', () => {
  for (const locale of ['zh-CN', 'en-US', 'zh-TW']) {
    const values = new Map();
    runPreload(values, { url: 'http://127.0.0.1:43123/desktop/setup' });
    // 初始化页面语言开关与 Umi 共用此正式存储项。
    values.set('umi_locale', locale);
    for (const url of ['http://127.0.0.1:43123/user/login', 'http://127.0.0.1:43123/user/login']) {
      const result = runPreload(values, { url });
      assert.equal(values.get('umi_locale'), locale);
      assert.equal(result.exposed.get('oobDesktop').localeBeforePage, locale);
      assert.deepEqual(result.writes, []);
    }
  }
});

test('无效语言恢复简中且不改写其它本地存储项', () => {
  for (const locale of ['', 'bn-BD', 'en-us', 'invalid']) {
    const values = new Map([['umi_locale', locale], ['fixture-preference', 'keep']]);
    runPreload(values);
    assert.equal(values.get('umi_locale'), 'zh-CN');
    assert.equal(values.get('fixture-preference'), 'keep');
  }
});

test('动态端口的新来源采用简中，不把旧来源偏好当作当前值', () => {
  const oldOrigin = new Map([['umi_locale', 'en-US']]);
  const newOrigin = new Map();
  runPreload(oldOrigin, { url: 'http://127.0.0.1:43123/user/login' });
  runPreload(newOrigin, { url: 'http://127.0.0.1:43124/user/login' });
  assert.equal(oldOrigin.get('umi_locale'), 'en-US');
  assert.equal(newOrigin.get('umi_locale'), 'zh-CN');
});

test('空白调试页或存储不可用仍建立既有桌面能力桥', () => {
  for (const options of [{ url: 'about:blank' }, { storageError: true }]) {
    const result = runPreload(new Map(), options);
    assert.deepEqual(result.writes, []);
    assert.equal(result.exposed.get('oobDesktop').value.version, 'fixture');
  }
});
