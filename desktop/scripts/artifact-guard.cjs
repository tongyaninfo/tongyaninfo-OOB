const { readdirSync, readFileSync } = require('node:fs');
const path = require('node:path');

/** 只检查发布副本路径，不读取私密内容，也不自动删除被拒绝的输入。 */
function assertPublicRuntime(directory) {
  function visit(current) {
    for (const entry of readdirSync(current, { withFileTypes: true })) {
      const name = entry.name.toLowerCase();
      const file = path.join(current, entry.name);
      if (name === '.local' || name === 'keys' || name === '.env' || name.startsWith('.env.')
        || /^appsettings(?:\..+)?\.(?:local|private)\.json$/i.test(name)
        || /\.(?:sqlite|db)(?:-wal|-shm)?$/i.test(name))
        throw new Error(`发布产物包含不允许分发的配置或运行数据路径：${path.relative(directory, file)}`);
      if (entry.isSymbolicLink()) throw new Error(`发布产物包含未审查的符号链接：${path.relative(directory, file)}`);
      if (entry.isDirectory()) visit(file);
    }
  }
  visit(directory);
}

/** 复用前端构建也必须核对产品版本，防止旧版 PC 混入新版桌面安装包。 */
function assertFrontendVersion(directory, expected) {
  const html = readFileSync(path.join(directory, 'index.html'), 'utf8');
  const versions = [...html.matchAll(/<meta\b[^>]*>/gi)].flatMap(([tag]) => {
    const attributes = Object.fromEntries([...tag.matchAll(/([\w-]+)\s*=\s*["']([^"']*)["']/g)]
      .map(([, name, value]) => [name.toLowerCase(), value]));
    return attributes.name === 'application-version' ? [attributes.content] : [];
  });
  if (versions.length !== 1 || versions[0] !== expected) {
    throw new Error(`PC 构建版本与产品版本 ${expected} 不一致或缺失，请重新构建 PC`);
  }
}

module.exports = { assertPublicRuntime, assertFrontendVersion };
