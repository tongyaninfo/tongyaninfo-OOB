import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { root, node, npm, run, main } from './lib.mjs';

// 只恢复已存在工程的锁定依赖；忽略生命周期脚本，避免自动执行 Git/hook 或下载工具。
main(() => {
  const args = process.argv.slice(2);
  const selected = args.length === 0 ? 'all' : args.length === 2 && args[0] === '--scope' ? args[1] : '';
  if (!['docs', 'frontend', 'backend', 'all'].includes(selected)) throw new Error('用法：--scope docs|frontend|backend|all（默认 all）');
  node('scripts/doctor.mjs', ['--scope', selected]);
  if (selected === 'docs') return;

  // 合同生成工具是独立轻量工程；首次运行生成锁文件，之后按锁安装。
  if (fs.existsSync(path.join(root, 'frontend/package-lock.json'))) {
    npm(['--prefix', 'frontend', 'ci', '--no-audit', '--no-fund']);
  } else {
    npm(['--prefix', 'frontend', 'install', '--no-audit', '--no-fund']);
  }
  run('dotnet', ['restore', 'backend/OobManagement.slnx']);
  run('dotnet', ['restore', 'backend/tools/OobContracts/OobContracts.csproj']);
  run('dotnet', ['restore', 'desktop/OobManagement.Desktop.slnx']);
  if (selected === 'frontend' || selected === 'all') {
    npm(['--prefix', 'frontend/PC', 'ci', '--ignore-scripts', '--no-audit', '--no-fund']);
    const result = spawnSync(process.execPath, [path.join(root, 'frontend/PC/node_modules/@umijs/max/bin/max.js'), 'setup'],
      { cwd: path.join(root, 'frontend/PC'), shell: false, stdio: 'inherit' });
    if (result.error || result.status !== 0) throw new Error('本地 Umi setup 失败，未安装全局工具。');
    npm(['--prefix', 'desktop', 'ci', '--ignore-scripts', '--no-audit', '--no-fund']);
  }
  if (selected === 'backend' || selected === 'all') {
    for (const project of ['backend/tests/OobManagement.Driver.Tests/OobManagement.Driver.Tests.csproj',
      'backend/tests/OobManagement.Integration.Tests/OobManagement.Integration.Tests.csproj']) {
      run('dotnet', ['restore', project]);
    }
  }
  console.log('依赖恢复完成；未执行 Git、hook、数据库迁移、设备读取或电源动作。');
  console.log('Electron 二进制需要另行显式下载：node desktop/node_modules/electron/install.js');
});
