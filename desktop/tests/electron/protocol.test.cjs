const { test } = require('node:test');
const assert = require('node:assert/strict');
const { parseReady, isTrustedNavigation, isAllowedPermission } = require('../../dist/main/protocol.js');

test('仅绑定本次子进程的完整 loopback 根地址', () => {
  const message = { type: 'ready', url: 'http://127.0.0.1:43123', initialized: false, instanceId: 'run-a' };
  assert.equal(parseReady(message, 'run-a').url, message.url);
  assert.equal(parseReady(message, 'run-b'), null);
  for (const url of ['https://example.com:43123', 'http://localhost:43123', 'http://127.0.0.1:43123/path', 'http://user@127.0.0.1:43123', 'http://127.0.0.1:43123/?secret=x', 'file:///tmp/index.html', 'http://127.0.0.1']) {
    assert.equal(parseReady({ ...message, url }, 'run-a'), null, url);
  }
});

test('页面导航不得改变本地宿主来源或携带用户信息', () => {
  const origin = 'http://127.0.0.1:43123';
  assert.equal(isTrustedNavigation(`${origin}/oob/servers`, origin), true);
  assert.equal(isTrustedNavigation(`${origin}/api/oob/console-sessions/1/frame/`, origin), true);
  assert.equal(isTrustedNavigation('http://127.0.0.1:43124/', origin), false);
  assert.equal(isTrustedNavigation('javascript:alert(1)', origin), false);
  assert.equal(isTrustedNavigation('http://user@127.0.0.1:43123/', origin), false);
});

test('可信控制台可捕获键鼠和全屏，跨来源 frame 与其它权限保持拒绝', () => {
  const origin = 'http://127.0.0.1:43123';
  const page = `${origin}/oob/consoles`;
  const frame = `${origin}/api/oob/console-sessions/1/frame/`;
  for (const permission of ['fullscreen', 'pointerLock', 'keyboardLock', 'clipboard-sanitized-write']) {
    assert.equal(isAllowedPermission(permission, page, frame, origin), true, permission);
    assert.equal(isAllowedPermission(permission, page, 'https://example.com/frame', origin), false);
    assert.equal(isAllowedPermission(permission, 'https://example.com/', frame, origin), false);
    assert.equal(isAllowedPermission(permission, page, 'http://127.0.0.1:43124/', origin), false);
  }
  for (const permission of ['clipboard-read', 'media', 'fileSystem', 'notifications', 'unknown'])
    assert.equal(isAllowedPermission(permission, page, frame, origin), false, permission);
});
