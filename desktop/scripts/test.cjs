const { spawnSync } = require('node:child_process');
const path = require('node:path');
const root = path.resolve(__dirname, '..');
const compiler = path.join(path.dirname(require.resolve('typescript/package.json')), 'bin/tsc');
for (const args of [[compiler, '-p', path.join(root, 'tsconfig.json')], ['--test', path.join(root, 'tests/electron/protocol.test.cjs'), path.join(root, 'tests/electron/host-process.test.cjs'), path.join(root, 'tests/electron/offline-assets.test.cjs'), path.join(root, 'tests/electron/artifact-guard.test.cjs'), path.join(root, 'tests/electron/locale.test.cjs'), path.join(root, 'tests/electron/setup-page.test.cjs')]]) {
  const result = spawnSync(process.execPath, args, { cwd: root, stdio: 'inherit' });
  if (result.status !== 0) process.exit(result.status ?? 1);
}
