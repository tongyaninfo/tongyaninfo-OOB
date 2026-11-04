import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { root, main } from './lib.mjs';

// 三条正式迁移链独立记录；检查只读取源码，不连接数据库或推断现场已应用状态。
export const chains = Object.freeze({
  postgresqlBusiness: {
    context: 'OobDbContext', provider: 'PostgreSQL',
    directory: 'backend/src/OobManagement.Infrastructure/Persistence/Migrations',
    historyTable: '__EFMigrationsHistory',
  },
  postgresqlOAuth: {
    context: 'McpOAuthDbContext', provider: 'PostgreSQL',
    directory: 'backend/src/OobManagement.Infrastructure/Persistence/OAuthMigrations',
    historyTable: '__McpOAuthMigrationsHistory',
  },
  sqlite: {
    context: 'SqliteOobDbContext', provider: 'SQLite',
    directory: 'desktop/backend/OobManagement.Desktop.Storage/Persistence/Migrations',
    historyTable: '__EFMigrationsHistory',
  },
});

const versionPattern = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/;
const migrationPattern = /^\d{14}_[A-Za-z][A-Za-z0-9_]*$/;
const digestPattern = /^[a-f0-9]{64}$/;
const json = (directory, name) => JSON.parse(fs.readFileSync(path.join(directory, name), 'utf8'));
const assert = (condition, message) => { if (!condition) throw new Error(message); };
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);

function version(value) {
  assert(typeof value === 'string' && versionPattern.test(value), `无效产品版本：${value}；要求稳定版本 X.Y.Z，不能带前导零`);
  return value;
}

function compare(a, b) {
  const left = a.split('.').map(BigInt), right = b.split('.').map(BigInt);
  for (let i = 0; i < 3; i++) if (left[i] !== right[i]) return left[i] < right[i] ? -1 : 1;
  return 0;
}

function date(value) {
  assert(typeof value === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(value)
    && Number.isFinite(Date.parse(value)) && new Date(value).toISOString().slice(0, 10) === value,
  `无效版本日期：${value}；要求真实日期 YYYY-MM-DD`);
  return value;
}

function publicFile(directory, name, prefix) {
  assert(typeof name === 'string' && name.startsWith(prefix) && !name.includes('\\')
    && !path.isAbsolute(name) && !name.split('/').some(part => !part || part === '.' || part === '..'),
  `非法台账路径：${name}`);
  const filename = path.join(directory, name);
  assert(fs.existsSync(filename) && fs.lstatSync(filename).isFile() && !fs.lstatSync(filename).isSymbolicLink(), `台账文件不存在：${name}`);
  return filename;
}

export function inventory(directory) {
  const result = {};
  for (const [key, definition] of Object.entries(chains)) {
    const sources = [];
    function visit(relative) {
      for (const entry of fs.readdirSync(path.join(directory, relative), { withFileTypes: true })) {
        assert(!entry.isSymbolicLink(), `迁移目录不接受符号链接：${relative}/${entry.name}`);
        const name = `${relative}/${entry.name}`;
        if (entry.isDirectory()) visit(name);
        else if (entry.isFile() && /\.(?:cs|sql)$/.test(entry.name) && !entry.name.endsWith('ModelSnapshot.cs')) sources.push(name);
      }
    }
    visit(definition.directory);
    const migrations = new Set(), files = {};
    for (const source of sources.sort()) {
      const buffer = fs.readFileSync(path.join(directory, source));
      files[source] = createHash('sha256').update(buffer).digest('hex');
      const body = buffer.toString('utf8');
      for (const match of body.matchAll(/^\s*\[Migration\("([^"\r\n]+)"\)\]/gm)) {
        const id = match[1];
        assert(migrationPattern.test(id), `${source}：无效 Migration ID ${id}`);
        assert(!migrations.has(id), `${source}：重复 Migration ID ${id}`);
        assert(path.basename(source) === `${id}.cs` || path.basename(source) === `${id}.Designer.cs`, `${source}：文件名与 Migration ID 不一致`);
        assert(new RegExp(`\\[DbContext\\(typeof\\(${definition.context}\\)\\)\\]`).test(body), `${source}：迁移未声明所属 ${definition.context}`);
        migrations.add(id);
      }
    }
    assert(migrations.size > 0, `${key}：正式迁移链为空`);
    for (const source of sources) {
      const match = path.basename(source).match(/^(\d{14}_[A-Za-z][A-Za-z0-9_]*?)(?:\.Designer)?\.cs$/);
      if (match) assert(migrations.has(match[1]), `${source}：迁移文件缺少对应 Migration 属性`);
    }
    for (const id of migrations) assert(Object.keys(files).some(name => path.basename(name) === `${id}.cs`), `${key}：缺少迁移实现 ${id}.cs`);
    result[key] = { ...definition, migrations: [...migrations].sort(), files };
  }
  return result;
}

function records(directory) {
  const location = path.join(directory, 'docs/releases');
  if (!fs.existsSync(location)) return [];
  return fs.readdirSync(location).filter(name => name.endsWith('.json')).map(name => {
    const release = json(directory, `docs/releases/${name}`);
    version(release.version);
    assert(name === `${release.version}.json`, `版本台账文件名与内容不一致：${name}`);
    return release;
  }).sort((a, b) => compare(a.version, b.version));
}

function checkHistory(directory, releases, current) {
  let previous = null;
  for (const release of releases) {
    const label = `版本 ${release.version}`;
    assert(release.formatVersion === 1, `${label}：不支持的台账格式`);
    date(release.date);
    assert(release.previousVersion === (previous?.version ?? null), `${label}：previousVersion 断链或跳版`);
    assert(!previous || compare(previous.version, release.version) < 0, `${label}：版本号必须递增`);
    assert(!previous || previous.date <= release.date, `${label}：版本日期不得早于前版`);
    // 开源版本不包含内部变更记录（docs/changes/），台账无 changeRecord 时跳过；其余台账校验保持一致。
    if (release.changeRecord) publicFile(directory, release.changeRecord, 'docs/changes/');
    assert(release.notes === `docs/releases/${release.version}.md`, `${label}：版本说明路径不一致`);
    publicFile(directory, release.notes, 'docs/releases/');
    assert(release.database && same(Object.keys(release.database).sort(), Object.keys(chains).sort()), `${label}：必须逐项登记三条正式迁移链`);
    for (const [key, definition] of Object.entries(chains)) {
      const chain = release.database[key], actual = current[key], before = previous?.database[key];
      assert(chain && Object.entries(definition).every(([field, value]) => chain[field] === value), `${label}/${key}：上下文、Provider、目录或历史表与正式链不一致`);
      assert(Array.isArray(chain.migrations) && chain.migrations.length > 0 && chain.migrations.every(id => typeof id === 'string' && migrationPattern.test(id))
        && same(chain.migrations, [...new Set(chain.migrations)].sort()), `${label}/${key}：迁移 ID 应为非空、有序且不重复的数组`);
      assert(same(actual.migrations.slice(0, chain.migrations.length), chain.migrations), `${label}/${key}：迁移链被改写、移除或插入历史`);
      assert(!before || same(chain.migrations.slice(0, before.migrations.length), before.migrations), `${label}/${key}：不能改写前版迁移链`);
      assert(chain.files && typeof chain.files === 'object' && !Array.isArray(chain.files) && Object.keys(chain.files).length > 0, `${label}/${key}：缺少历史源码摘要`);
      for (const [name, digest] of Object.entries(chain.files)) {
        assert(name.startsWith(`${definition.directory}/`) && actual.files[name] !== undefined && digestPattern.test(digest), `${label}/${key}：非法或已丢失的历史文件 ${name}`);
        assert(actual.files[name] === digest, `${label}/${key}：历史迁移文件已改写 ${name}`);
      }
      if (before) for (const [name, digest] of Object.entries(before.files)) assert(chain.files[name] === digest, `${label}/${key}：不能移除或改写前版摘要 ${name}`);
      // 即使曾手工漏填 files，也必须保证每条迁移实现及其 Designer 被当前快照覆盖。
      for (const id of chain.migrations) {
        for (const name of Object.keys(actual.files).filter(name => path.basename(name) === `${id}.cs` || path.basename(name) === `${id}.Designer.cs`)) {
          assert(chain.files[name] === actual.files[name], `${label}/${key}：迁移摘要漏登记 ${name}`);
        }
      }
      const added = chain.migrations.slice(before?.migrations.length ?? 0);
      assert(Array.isArray(chain.changes) && same(chain.changes.map(change => change?.migrationId), added), `${label}/${key}：changes 必须逐项说明本版新增迁移；无变更填 []`);
      for (const change of chain.changes) assert(typeof change.summary === 'string' && change.summary.trim().length > 0 && !/待完善|TODO|待填写/i.test(change.summary), `${label}/${key}/${change.migrationId}：请补全数据库变更摘要`);
    }
    previous = release;
  }
}

function checkDesktopVersion(directory, expected) {
  const manifest = json(directory, 'desktop/package.json'), lock = json(directory, 'desktop/package-lock.json');
  assert(manifest.version === expected && lock.version === expected && lock.packages?.['']?.version === expected,
    `桌面 package.json/package-lock.json 根版本必须与 VERSION ${expected} 一致（仅同步根元数据，不升级依赖）`);
}

export function check(directory = root) {
  const expected = version(fs.readFileSync(path.join(directory, 'VERSION'), 'utf8').trim());
  checkDesktopVersion(directory, expected);
  const current = inventory(directory), releases = records(directory);
  assert(releases.length > 0, '缺少版本台账；请先 record 并补全版本说明');
  checkHistory(directory, releases, current);
  const latest = releases.at(-1);
  assert(latest.version === expected, `VERSION ${expected} 与最新台账 ${latest.version} 不一致`);
  for (const key of Object.keys(chains)) {
    assert(same(latest.database[key].migrations, current[key].migrations), `${key}：存在未登记的新迁移`);
    assert(same(Object.keys(latest.database[key].files).sort(), Object.keys(current[key].files).sort()), `${key}：存在未登记或丢失的迁移源码/辅助脚本`);
  }
  return { version: expected, releases: releases.length, migrations: Object.fromEntries(Object.keys(chains).map(key => [key, current[key].migrations.length])) };
}

export function record(directory = root, options) {
  const requested = version(options.version), releaseDate = date(options.date);
  assert(fs.readFileSync(path.join(directory, 'VERSION'), 'utf8').trim() === requested, 'record 目标必须与 VERSION 一致；本工具不自动升版');
  checkDesktopVersion(directory, requested);
  const current = inventory(directory), releases = records(directory);
  assert(!releases.some(release => release.version === requested), `台账 ${requested} 已存在，禁止覆盖`);
  checkHistory(directory, releases, current);
  const previous = releases.at(-1);
  assert(!previous || compare(previous.version, requested) < 0, '新版本必须大于全部历史版本');
  assert(!previous || previous.date <= releaseDate, '新版本日期不得早于前版');
  const changeRecord = options.changeRecord ?? (requested === '1.0.0'
    ? 'docs/changes/2026-11-03-versioning-and-upgrade-baseline.md' : `docs/changes/${releaseDate}-release-${requested}.md`);
  assert(typeof changeRecord === 'string' && /^docs\/changes\/[^/]+\.md$/.test(changeRecord) && !changeRecord.includes('..'), 'change-record 必须是 docs/changes/ 下的 Markdown 相对路径');
  const database = {};
  for (const [key, chain] of Object.entries(current)) {
    const before = previous?.database[key]?.migrations.length ?? 0;
    database[key] = { ...chain, changes: chain.migrations.slice(before).map(migrationId => ({ migrationId, summary: '待完善：说明表/字段、数据转换、兼容与恢复影响' })) };
  }
  const release = { formatVersion: 1, version: requested, previousVersion: previous?.version ?? null,
    date: releaseDate, changeRecord, notes: `docs/releases/${requested}.md`, database };
  fs.mkdirSync(path.join(directory, 'docs/releases'), { recursive: true });
  // 排他创建避免并行任务或误操作覆盖已经登记的版本；历史文件永不重算覆盖。
  fs.writeFileSync(path.join(directory, `docs/releases/${requested}.json`), `${JSON.stringify(release, null, 2)}\n`, { flag: 'wx' });
  return release;
}

export function run(args, directory = root) {
  if (args.length === 1 && args[0] === 'check') {
    const result = check(directory);
    console.log(`版本检查通过：${result.version}，${result.releases} 个版本，迁移链 ${Object.values(result.migrations).join('/')}；仅源码基线，未连接数据库。`);
    return result;
  }
  if (args[0] === 'record') {
    const options = {};
    const names = { '--version': 'version', '--date': 'date', '--change-record': 'changeRecord' };
    assert(args.length % 2 === 1, 'record 参数必须成对提供');
    for (let i = 1; i < args.length; i += 2) {
      const name = names[args[i]];
      assert(name && options[name] === undefined && args[i + 1], `无效或重复参数：${args[i]}`);
      options[name] = args[i + 1];
    }
    const result = record(directory, options);
    console.log(`已创建 docs/releases/${result.version}.json 草稿；请补全 changes 摘要、版本说明与变更记录，然后运行 check。此命令未发布版本、未升级数据库。`);
    return result;
  }
  throw new Error('用法：node scripts/releases.mjs check | record --version X.Y.Z --date YYYY-MM-DD [--change-record docs/changes/<name>.md]');
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main(() => run(process.argv.slice(2)));
