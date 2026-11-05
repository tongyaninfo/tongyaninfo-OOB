const path = require('node:path');
if (!process.env.OOB_DESKTOP_STAGE || !process.env.OOB_DESKTOP_OUTPUT) throw new Error('请通过 npm run package 执行打包');

module.exports = {
  // 安装包版本以产品 VERSION 为准；开发包及原锁根元数据由版本门禁同步核对。
  extraMetadata: { version: require('../scripts/product-version.cjs') },
  appId: 'com.tongyaninfo.oob-management',
  productName: 'OutOfBandManagement',
  executableName: 'OutOfBandManagement',
  artifactName: '${productName}-${version}-${os}-${arch}.${ext}',
  directories: { output: process.env.OOB_DESKTOP_OUTPUT, buildResources: 'resources' },
  asar: true,
  ...(process.env.OOB_DESKTOP_ELECTRON_DIST ? { electronDist: process.env.OOB_DESKTOP_ELECTRON_DIST } : {}),
  npmRebuild: false,
  files: ['dist/**/*.js', 'package.json'],
  extraResources: [
    { from: path.join(process.env.OOB_DESKTOP_STAGE, 'host'), to: 'host' },
    { from: 'resources/icon.png', to: 'icon.png' },
    { from: 'resources/trayTemplate.png', to: 'trayTemplate.png' },
  ],
  // 本地验证使用 ad-hoc 签名，不读取用户签名身份；正式公证发布另按明确授权配置。
  mac: {
    target: ['dmg', 'zip'], category: 'public.app-category.utilities', icon: 'resources/icon.icns', identity: '-', hardenedRuntime: true,
    entitlements: 'resources/entitlements.mac.plist', entitlementsInherit: 'resources/entitlements.mac.plist',
  },
  win: { target: ['nsis'], icon: 'resources/icon.ico' },
  nsis: { oneClick: false, perMachine: false, allowToChangeInstallationDirectory: true, deleteAppDataOnUninstall: false },
  linux: { target: ['AppImage', 'deb', 'rpm'], icon: 'resources/icon.png', category: 'Utility', synopsis: '带外管理系统本地桌面版', maintainer: 'tongyaninfo contributors <noreply@tongyaninfo.com>' },
  // 旧 AppImage 工具默认追加 --no-sandbox；显式空参数保留桌面页面既定的沙箱边界。
  appImage: { executableArgs: [] },
};
