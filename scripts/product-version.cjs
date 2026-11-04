const fs = require('node:fs');
const path = require('node:path');

// 产品版本与上游模板/依赖版本分开，PC 和桌面打包复用同一个来源。
const version = fs.readFileSync(path.join(__dirname, '..', 'VERSION'), 'utf8').trim();
if (!/^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.test(version)) {
  throw new Error('VERSION 必须为不含前导零的稳定三段版本，例如 1.0.0');
}

module.exports = version;
