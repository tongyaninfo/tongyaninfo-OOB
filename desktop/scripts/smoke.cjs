const { spawn } = require('node:child_process');
const { mkdtempSync } = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { desktop } = require('./runtime.cjs');

const args = process.argv.slice(2);
const value = name => { const index = args.indexOf(name); return index >= 0 ? args[index + 1] : undefined; };
const application = value('--application');
const host = value('--host') ?? process.env.OOB_DESKTOP_HOST_PATH;
if (!application && !host) throw new Error('开发冒烟需 --host；安装产物冒烟需 --application');
const data = value('--data') ?? mkdtempSync(path.join(os.tmpdir(), 'oob-desktop-smoke-data-'));
const screenshot = value('--screenshot') ?? path.join(os.tmpdir(), `oob-desktop-smoke-${Date.now()}.png`);
const executable = application ?? require('electron');
const child = spawn(executable, application ? [] : [desktop], {
  stdio: ['ignore', 'pipe', 'pipe'],
  env: { ...process.env, OOB_DESKTOP_DATA: data, ...(host ? { OOB_DESKTOP_HOST_PATH: host } : {}), OOB_DESKTOP_SMOKE: '1', OOB_DESKTOP_SCREENSHOT: screenshot },
});
let output = ''; let report; let failure; let release;
child.stdout.on('data', chunk => {
  output += chunk.toString();
  for (const line of output.split('\n').slice(0, -1)) {
    try { const item = JSON.parse(line); if (item.type === 'desktop-smoke') report = item; if (item.type === 'desktop-smoke-error') failure = item; if (item.type === 'desktop-release') release = item; } catch { }
  }
  output = output.slice(output.lastIndexOf('\n') + 1);
});
child.stderr.on('data', () => undefined);
const timeout = setTimeout(() => { child.kill('SIGTERM'); process.stderr.write('桌面冒烟超时\n'); process.exitCode = 1; }, 90000);
child.once('error', () => { clearTimeout(timeout); process.stderr.write('无法启动桌面应用\n'); process.exitCode = 1; });
child.once('exit', code => {
  clearTimeout(timeout);
  if (code !== 0 || !report || !release?.released || !release.blurObserved || !report.initialized || !report.authenticated || !report.fontsLoaded || !report.keyboardRouting || report.body < 1 || report.node !== 'undefined'
    || report.consoleErrors?.length || report.failedRequests?.length) {
    if (report || failure) process.stdout.write(`${JSON.stringify({ ...(report ?? failure), data, screenshot })}\n`);
    process.stderr.write('桌面冒烟失败：页面、沙箱或退出状态不符合预期\n');
    process.exitCode = 1;
  } else process.stdout.write(`${JSON.stringify({ ...report, releaseBeforeShutdown: release.released && release.blurObserved, data, screenshot })}\n`);
});
