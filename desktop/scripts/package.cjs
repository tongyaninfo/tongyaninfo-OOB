const { existsSync, mkdtempSync, symlinkSync } = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { desktop, npm, prepareRuntime, run } = require('./runtime.cjs');
const version = require('../../scripts/product-version.cjs');

const args = process.argv.slice(2);
const value = name => { const index = args.indexOf(name); return index >= 0 ? args[index + 1] : undefined; };
const platform = value('--platform') ?? process.platform;
const arch = value('--arch') ?? process.arch;
if (!['darwin', 'win32', 'linux'].includes(platform)) throw new Error('--platform 必须是 darwin、win32 或 linux');
if (!['x64', 'arm64'].includes(arch)) throw new Error('--arch 必须是 x64 或 arm64');
const format = value('--format');
if (format && !({ darwin: ['dmg', 'zip'], win32: ['nsis', 'portable'], linux: ['AppImage', 'deb', 'rpm'] }[platform].includes(format))) throw new Error('安装格式与目标平台不匹配');
npm(['run', 'build'], desktop);
const runtime = prepareRuntime({ platform, arch, skipFrontendBuild: args.includes('--skip-frontend-build') });
const output = process.env.OOB_DESKTOP_OUTPUT ?? mkdtempSync(path.join(os.tmpdir(), 'oob-desktop-packages-'));
process.env.OOB_DESKTOP_STAGE = runtime.stage;
process.env.OOB_DESKTOP_OUTPUT = output;
const electronDist = path.join(path.dirname(require.resolve('electron/package.json')), 'dist');
if (platform === process.platform && arch === process.arch && existsSync(path.join(electronDist, 'version')))
  process.env.OOB_DESKTOP_ELECTRON_DIST = electronDist;
const builder = path.join(path.dirname(require.resolve('electron-builder/package.json')), 'cli.js');
const target = { darwin: '--mac', win32: '--win', linux: '--linux' }[platform];
const builderArgs = [builder, '--config', path.join(desktop, 'electron-builder.cjs'), target];
const directoryOnly = args.includes('--dir');
const nativeDmg = platform === 'darwin' && !directoryOnly && (!format || format === 'dmg');
if (format && !nativeDmg) builderArgs.push(format);
builderArgs.push(`--${arch}`);
if (directoryOnly || nativeDmg) builderArgs.push('--dir');
// 默认只在本地产出文件，禁止打包工具根据 CI 或仓库配置自动发布。
builderArgs.push('--publish', 'never');
run(process.execPath, builderArgs, desktop);
if (nativeDmg) {
  // 使用 macOS 自带磁盘映像工具，不额外下载并运行 dmgbuild/Python 工具链。
  if (process.platform !== 'darwin') throw new Error('DMG 需要在 macOS 构建');
  const appDirectory = path.join(output, arch === 'arm64' ? 'mac-arm64' : 'mac');
  const application = path.join(appDirectory, 'OutOfBandManagement.app');
  if (!existsSync(application)) throw new Error('未找到已签名的 macOS 应用');
  symlinkSync('/Applications', path.join(appDirectory, 'Applications'), 'dir');
  const image = path.join(output, `OutOfBandManagement-${version}-mac-${arch}.dmg`);
  run('hdiutil', ['create', '-volname', 'OutOfBandManagement', '-srcfolder', appDirectory, '-format', 'UDZO', '-fs', 'HFS+', image], desktop);
  run('hdiutil', ['verify', image], desktop);
}
process.stdout.write(`${JSON.stringify({ type: 'desktop-package', platform, arch, rid: runtime.rid, output })}\n`);
