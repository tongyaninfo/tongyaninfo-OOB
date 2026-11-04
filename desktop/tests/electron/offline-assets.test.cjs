const { test } = require('node:test');
const assert = require('node:assert/strict');
const { mkdtempSync, writeFileSync, readFileSync, mkdirSync, existsSync } = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const { localizeFrontend } = require('../../scripts/offline-assets.cjs');
const manifest = require('../../resources/offline/manifest.json');

test('桌面构建递归本地化 CSS/JS/HTML，保持其它业务远端地址', () => {
  const directory = mkdtempSync(path.join(os.tmpdir(), 'oob-offline-assets-test-'));
  const asset = manifest.assets[0];
  mkdirSync(path.join(directory, 'nested'));
  writeFileSync(path.join(directory, 'index.html'), `<img src="${asset.url}">`);
  writeFileSync(path.join(directory, 'nested/style.css'), `body{background:url(${asset.url.replace('https:', '')})}`);
  writeFileSync(path.join(directory, 'nested/app.js'), `const logo="${asset.url}",device="https://192.0.2.10";`);
  const result = localizeFrontend(directory);
  assert.equal(result.assets, 12);
  assert.equal(result.changedFiles, 3);
  const url = `/desktop-assets/${asset.publicFile ?? asset.file}`;
  assert.equal(readFileSync(path.join(directory, 'index.html'), 'utf8'), `<img src="${url}">`);
  assert.equal(readFileSync(path.join(directory, 'nested/style.css'), 'utf8'), `body{background:url(${url})}`);
  assert.equal(readFileSync(path.join(directory, 'nested/app.js'), 'utf8'), `const logo="${url}",device="https://192.0.2.10";`);
  for (const item of manifest.assets) assert.equal(existsSync(path.join(directory, 'desktop-assets', item.publicFile ?? item.file)), true);
});

test('模板新增未登记 CDN 资源时阻断打包', () => {
  const directory = mkdtempSync(path.join(os.tmpdir(), 'oob-offline-assets-unknown-'));
  writeFileSync(path.join(directory, 'index.html'), '<img src="https://mdn.alipayobjects.com/new-template-asset.png">');
  assert.throws(() => localizeFrontend(directory), /尚未本地化/);
});
