const { createHash } = require('node:crypto');
const { copyFileSync, mkdirSync, readFileSync, readdirSync, writeFileSync } = require('node:fs');
const path = require('node:path');

const resourceDirectory = path.resolve(__dirname, '../resources/offline');
const staticCdnPattern = /(?:https?:)?\/\/(?:mdn|gw)\.alipayobjects\.com\/[^\s"'<>\\)]+/g;

/** 只改写桌面 staging 的固定模板引用；服务端 PC 产物和业务地址不作改动。 */
function localizeFrontend(webRoot) {
  const manifest = JSON.parse(readFileSync(path.join(resourceDirectory, 'manifest.json'), 'utf8'));
  const destination = path.join(webRoot, 'desktop-assets');
  mkdirSync(destination, { recursive: true });
  const replacements = [];
  for (const asset of manifest.assets) {
    const publicFile = asset.publicFile ?? asset.file;
    if (path.basename(asset.file) !== asset.file || path.basename(publicFile) !== publicFile
      || !/^https:\/\/(mdn|gw)\.alipayobjects\.com\//.test(asset.url)) throw new Error('桌面固定资源清单无效');
    const source = path.join(resourceDirectory, asset.file);
    const bytes = readFileSync(source);
    // Windows 检出可能把 SVG 等文本模板转换为 CRLF；按规范化文本计算摘要并写入副本，避免哈希误报。
    const normalized = /\.(?:svg|txt|json|html?|css|js)$/i.test(asset.file)
      ? Buffer.from(bytes.toString('utf8').replace(/\r\n/g, '\n')) : bytes;
    if (createHash('sha256').update(normalized).digest('hex') !== asset.sha256 || normalized.length !== asset.bytes)
      throw new Error(`桌面固定资源摘要不一致：${asset.file}`);
    if (normalized === bytes) copyFileSync(source, path.join(destination, publicFile));
    else writeFileSync(path.join(destination, publicFile), normalized);
    replacements.push([asset.url, `/desktop-assets/${publicFile}`], [asset.url.replace(/^https:/, ''), `/desktop-assets/${publicFile}`]);
  }
  let changedFiles = 0;
  function visit(directory) {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const file = path.join(directory, entry.name);
      if (file === destination) continue;
      if (entry.isDirectory()) { visit(file); continue; }
      if (!entry.isFile() || !/\.(js|css|html|json)$/i.test(entry.name)) continue;
      const original = readFileSync(file, 'utf8');
      let content = original;
      for (const [url, local] of replacements) content = content.split(url).join(local);
      if ([...content.matchAll(staticCdnPattern)].length) throw new Error(`模板新增了尚未本地化的静态资源：${path.relative(webRoot, file)}`);
      if (content !== original) { writeFileSync(file, content); changedFiles++; }
    }
  }
  visit(webRoot);
  return { assets: manifest.assets.length, changedFiles };
}

module.exports = { localizeFrontend };

/** 服务端默认头像是 API 返回值，仅在桌面发布副本中改为清单里的同一图片。 */
function localizeDesktopSettings(settingsFile) {
  const settings = JSON.parse(readFileSync(settingsFile, 'utf8'));
  const manifest = JSON.parse(readFileSync(path.join(resourceDirectory, 'manifest.json'), 'utf8'));
  const avatar = manifest.assets.find(asset => asset.url === settings.Appearance?.DefaultAvatar);
  if (avatar) {
    settings.Appearance.DefaultAvatar = `/desktop-assets/${avatar.publicFile ?? avatar.file}`;
    writeFileSync(settingsFile, `${JSON.stringify(settings, null, 2)}\n`);
  }
}
module.exports.localizeDesktopSettings = localizeDesktopSettings;
