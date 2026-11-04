const { spawnSync } = require('node:child_process');
const { cpSync, existsSync, mkdirSync, mkdtempSync } = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { localizeFrontend, localizeDesktopSettings } = require('./offline-assets.cjs');
const { assertPublicRuntime, assertFrontendVersion } = require('./artifact-guard.cjs');
const productVersion = require('../../scripts/product-version.cjs');

const desktop = path.resolve(__dirname, '..');
const repository = path.resolve(desktop, '..');

function run(command, args, cwd) {
  const result = spawnSync(command, args, { cwd, stdio: 'inherit', windowsHide: true });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`构建命令失败：${path.basename(command)}，退出码 ${result.status ?? 'unknown'}`);
}

function npm(args, cwd) {
  // npm_execpath 由 npm 运行环境提供，直接调用 JS 入口兼容含空格的 Windows 路径。
  const npmCli = process.env.npm_execpath;
  if (!npmCli) throw new Error('请通过 npm run 执行桌面脚本');
  run(process.execPath, [npmCli, ...args], cwd);
}

function runtimeIdentifier(platform = process.platform, arch = process.arch) {
  const prefix = { darwin: 'osx', win32: 'win', linux: 'linux' }[platform];
  if (!prefix || !['x64', 'arm64'].includes(arch)) throw new Error('当前操作系统或架构不在桌面发布范围');
  return `${prefix}-${arch}`;
}

function prepareRuntime({ platform = process.platform, arch = process.arch, skipFrontendBuild = false } = {}) {
  // 打包前校验产品版本和迁移台账，阻断旧元数据或遗漏迁移进入安装包。
  run(process.execPath, [path.join(repository, 'scripts/releases.mjs'), 'check'], repository);
  const rid = runtimeIdentifier(platform, arch);
  // 每次使用新的仓库外 staging，避免清理已有文件，也避免混入旧版静态分包。
  const stage = mkdtempSync(path.join(os.tmpdir(), 'oob-desktop-stage-'));
  const host = path.join(stage, 'host');
  mkdirSync(host);
  if (!skipFrontendBuild) npm(['run', 'build'], path.join(repository, 'frontend/PC'));
  const frontend = path.join(repository, 'frontend/PC/dist');
  if (!existsSync(path.join(frontend, 'index.html'))) throw new Error('未找到 PC 构建入口');
  assertFrontendVersion(frontend, productVersion);
  run('dotnet', [
    'publish', path.join(desktop, 'backend/OobManagement.Desktop.Host/OobManagement.Desktop.Host.csproj'),
    '-c', 'Release', '-r', rid, '--self-contained', 'true',
    '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-o', host,
  ], repository);
  assertPublicRuntime(host);
  cpSync(frontend, path.join(host, 'wwwroot'), { recursive: true });
  const offline = localizeFrontend(path.join(host, 'wwwroot'));
  localizeDesktopSettings(path.join(host, 'appsettings.json'));
  assertPublicRuntime(host);
  process.stdout.write(`已验证并本地化 ${offline.assets} 项模板资源，更新 ${offline.changedFiles} 个桌面静态产物。\n`);
  return { stage, host, rid, executable: path.join(host, `OobManagement.Desktop.Host${platform === 'win32' ? '.exe' : ''}`) };
}

module.exports = { desktop, repository, run, npm, runtimeIdentifier, prepareRuntime };
