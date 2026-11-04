const { spawn } = require('node:child_process');
const { desktop, npm, prepareRuntime } = require('./runtime.cjs');
const electron = require('electron');
const skipFrontendBuild = process.argv.includes('--skip-frontend-build');
npm(['run', 'build'], desktop);
const runtime = prepareRuntime({ skipFrontendBuild });
const child = spawn(electron, [desktop], {
  stdio: 'inherit', env: { ...process.env, OOB_DESKTOP_HOST_PATH: runtime.executable }, windowsHide: true,
});
child.once('error', error => { process.stderr.write(`${error.message}\n`); process.exitCode = 1; });
child.once('exit', code => { process.exitCode = code ?? 1; });
