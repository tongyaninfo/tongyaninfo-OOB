import fs from 'node:fs';
import path from 'node:path';
import { root, readJson, run, npm, main } from './lib.mjs';

// 只读取真实工程清单检查工具链；不安装依赖、不访问网络或设备。
main(() => {
  const args = process.argv.slice(2);
  const selected = args.length === 0 ? 'all' : args.length === 2 && args[0] === '--scope' ? args[1] : '';
  if (!['docs', 'frontend', 'backend', 'all'].includes(selected)) throw new Error('用法：--scope docs|frontend|backend|all（默认 all）');
  const errors = [];
  const check = (label, action) => {
    try { console.log('[通过] ' + label + '：' + action()); }
    catch (error) { errors.push(label + '：' + error.message); }
  };
  const pc = readJson('frontend/PC/package.json');

  check('Node', () => {
    const required = pc.engines?.node ?? '';
    const match = /^>=(\d+)\.(\d+)\.(\d+)$/.exec(required);
    if (!match) throw new Error('请核对 frontend/PC/package.json 的 engines.node（要求 >=X.Y.Z）');
    const minimum = match.slice(1).map(Number);
    const actual = process.versions.node.split('.').map(Number);
    const compatible = actual[0] > minimum[0]
      || (actual[0] === minimum[0] && (actual[1] > minimum[1]
        || (actual[1] === minimum[1] && actual[2] >= minimum[2])));
    if (!compatible) throw new Error('要求 ' + required + '，当前 ' + process.versions.node);
    return process.versions.node;
  });

  if (selected !== 'docs') {
    check('npm', () => npm(['--version'], true));
    check('.NET SDK', () => {
      const expected = readJson('global.json').sdk;
      const actual = run('dotnet', ['--version'], true);
      const a = actual.split('.').map(Number), e = expected.version.split('.').map(Number);
      const sameBand = a[0] === e[0] && a[1] === e[1] && Math.floor(a[2] / 100) === Math.floor(e[2] / 100) && a[2] >= e[2];
      if (actual !== expected.version && !(expected.rollForward === 'latestPatch' && sameBand)) {
        throw new Error('要求 ' + expected.version + '/' + expected.rollForward + '，当前 ' + actual);
      }
      return actual;
    });
    check('工程清单', () => {
      const files = [
        'backend/OobManagement.slnx',
        'backend/src/OobManagement.Api/OobManagement.Api.csproj',
        'backend/tests/OobManagement.Driver.Tests/OobManagement.Driver.Tests.csproj',
        'backend/tests/OobManagement.Integration.Tests/OobManagement.Integration.Tests.csproj',
        'backend/tools/OobContracts/OobContracts.csproj',
        'frontend/PC/package.json', 'frontend/PC/package-lock.json',
        'desktop/package.json', 'desktop/package-lock.json', 'desktop/OobManagement.Desktop.slnx',
      ];
      for (const file of files) if (!fs.existsSync(path.join(root, file))) throw new Error('缺少 ' + file);
      return '后端四层、驱动/集成测试、PC 与桌面工程入口均存在';
    });
  }
  console.log('doctor 不执行安装、网络访问、Git、数据库迁移、服务启动或设备访问。');
  errors.forEach((error) => console.error('[失败] ' + error));
  if (errors.length) throw new Error('环境检查失败 ' + errors.length + ' 项，不能记为通过。');
});
