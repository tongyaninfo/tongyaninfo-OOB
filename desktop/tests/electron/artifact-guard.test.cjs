const { test } = require('node:test');
const assert = require('node:assert/strict');
const { mkdtempSync, writeFileSync, mkdirSync } = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const { assertPublicRuntime, assertFrontendVersion } = require('../../scripts/artifact-guard.cjs');

test('拒绝私有配置和运行数据文件，保留SQLite原生库及公开配置', () => {
  const allowed = mkdtempSync(path.join(os.tmpdir(), 'oob-artifact-allowed-'));
  for (const name of ['appsettings.json', 'e_sqlite3.dll', 'libe_sqlite3.dylib', 'libe_sqlite3.so']) writeFileSync(path.join(allowed, name), 'fixture');
  assert.doesNotThrow(() => assertPublicRuntime(allowed));
  for (const name of ['.env', '.env.production', 'appsettings.Development.Local.json', 'appsettings.Private.json', 'oob.sqlite', 'oob.sqlite-wal', 'oob.sqlite-shm', 'oob.db']) {
    const directory = mkdtempSync(path.join(os.tmpdir(), 'oob-artifact-refused-'));
    writeFileSync(path.join(directory, name), 'fixture');
    assert.throws(() => assertPublicRuntime(directory), /不允许分发/);
  }
  for (const name of ['.local', 'keys']) {
    const directory = mkdtempSync(path.join(os.tmpdir(), 'oob-artifact-refused-directory-'));
    mkdirSync(path.join(directory, name));
    assert.throws(() => assertPublicRuntime(directory), /不允许分发/);
  }
});

test('复用 PC 产物时拒绝旧版本、缺失及冲突的版本元数据', () => {
  const directory = mkdtempSync(path.join(os.tmpdir(), 'oob-frontend-version-'));
  for (const html of [
    '<meta name="application-version" content="1.0.0">',
    "<meta content='1.0.0' name='application-version'/>",
  ]) {
    writeFileSync(path.join(directory, 'index.html'), html);
    assert.doesNotThrow(() => assertFrontendVersion(directory, '1.0.0'));
  }
  for (const html of [
    '<meta name="application-version" content="1.0.0">',
    '<title>通研带外系统管理</title>',
    '<meta name="application-version" content="1.0.1"><meta name="application-version" content="1.0.0">',
  ]) {
    writeFileSync(path.join(directory, 'index.html'), html);
    assert.throws(() => assertFrontendVersion(directory, '1.0.1'), /不一致或缺失/);
  }
});
