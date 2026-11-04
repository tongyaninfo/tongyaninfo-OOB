const { test } = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const { HostProcess } = require('../../dist/main/host-process.js');

const fixture = path.join(__dirname, 'fixture-host.cjs');
function create(mode) {
  return new HostProcess({ executable: process.execPath, arguments: [fixture, mode], dataDirectory: __dirname, startupTimeoutMs: 400, shutdownTimeoutMs: 500 });
}

test('实际父子进程通过管道启动并完成优雅退出', async () => {
  const host = create('ready');
  const ready = await host.start();
  assert.equal(ready.url, 'http://127.0.0.1:43123');
  assert.equal(ready.initialized, false);
  assert.ok(host.launchSecret.length >= 32);
  await host.stop();
  await host.stop();
});

test('宿主在就绪前退出时启动失败，不伪造就绪', async () => {
  await assert.rejects(create('exit').start(), /就绪前结束/);
});

test('宿主报告外部来源时拒绝并停止该子进程', async () => {
  await assert.rejects(create('invalid').start(), /启动超时/);
});

test('可执行文件缺失时正常返回失败，不因缺少 exit 事件卡住退出', async () => {
  const host = new HostProcess({ executable: path.join(__dirname, 'missing-desktop-host'), dataDirectory: __dirname, startupTimeoutMs: 200, shutdownTimeoutMs: 100 });
  await assert.rejects(host.start(), /无法启动本地服务/);
});
