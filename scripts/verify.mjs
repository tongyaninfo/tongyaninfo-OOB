import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { root, node, npm, run, main } from './lib.mjs';

// 统一检查只调用当前真实工程；必需项失败立即停止，不以跳过代替通过。
main(() => {
  const args = process.argv.slice(2);
  const selected = args.length === 0 ? 'all' : args.length === 2 && args[0] === '--scope' ? args[1] : '';
  if (!['docs', 'frontend', 'backend', 'all'].includes(selected)) throw new Error('用法：--scope docs|frontend|backend|all（默认 all）');
  node('scripts/doctor.mjs', ['--scope', selected]);
  for (const entry of fs.readdirSync(path.join(root, 'scripts'), { withFileTypes: true })) {
    if (entry.isFile() && /\.(mjs|cjs)$/.test(entry.name)) run(process.execPath, ['--check', path.join(root, 'scripts', entry.name)]);
  }
  node('scripts/check-docs.mjs');
  if (selected === 'docs') {
    console.log('通过：文档路径、根脚本语法与环境检查。未执行应用构建、合同或数据库业务测试。');
    return;
  }
  if (selected === 'backend' || selected === 'all') {
    run('dotnet', ['build', 'backend/OobManagement.slnx']);
    run('dotnet', ['test', 'backend/tests/OobManagement.Driver.Tests/OobManagement.Driver.Tests.csproj']);
    // 集成测试需要真实 PostgreSQL 17 + TimescaleDB；未显式开启时如实跳过，不记作通过。
    if (process.env.OOB_INTEGRATION_DATABASE === '1') {
      run('dotnet', ['test', 'backend/tests/OobManagement.Integration.Tests/OobManagement.Integration.Tests.csproj']);
    } else {
      console.log('[跳过] 后端集成测试：需要真实 Testing 数据库。设置 OOB_INTEGRATION_DATABASE=1 并完成数据库初始化后单独执行。');
    }
    run('dotnet', ['build', 'desktop/OobManagement.Desktop.slnx']);
    run('dotnet', ['test', 'desktop/tests/OobManagement.Desktop.Tests/OobManagement.Desktop.Tests.csproj']);
  }
  node('scripts/oob-contracts.mjs', ['check']);
  if (selected === 'frontend' || selected === 'all') {
    const setup = spawnSync(process.execPath, [path.join(root, 'frontend/PC/node_modules/@umijs/max/bin/max.js'), 'setup'],
      { cwd: path.join(root, 'frontend/PC'), shell: false, stdio: 'inherit' });
    if (setup.error || setup.status !== 0) throw new Error('PC 本地 Umi setup 失败，请先执行 bootstrap。');
    npm(['--prefix', 'frontend/PC', 'run', 'lint']);
    npm(['--prefix', 'frontend/PC', 'test']);
    npm(['--prefix', 'frontend/PC', 'run', 'build']);
    npm(['--prefix', 'desktop', 'run', 'typecheck']);
    npm(['--prefix', 'desktop', 'test']);
    npm(['--prefix', 'desktop', 'run', 'build']);
  }
  console.log('通过：' + selected + ' 范围的当前工程必需检查。');
  console.log('边界：自动测试使用模拟带外驱动，不访问真机、不执行电源动作；不能替代浏览器、真实设备与人工电源验收。');
});
