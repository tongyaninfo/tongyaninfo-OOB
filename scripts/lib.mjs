import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

export const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
export const readJson = (name) => JSON.parse(fs.readFileSync(path.join(root, name), 'utf8'));

// 用参数数组启动真实程序；不拼接 shell，支持含空格的 Windows 路径。
export function run(command, args, capture = false) {
  const result = spawnSync(command, args, {
    cwd: root, shell: false, stdio: capture ? 'pipe' : 'inherit', encoding: 'utf8',
    maxBuffer: 16 * 1024 * 1024,
  });
  if (result.error) throw new Error(`无法执行 ${path.basename(command)}：${result.error.message}`);
  if (result.status !== 0) {
    throw new Error(`${path.basename(command)} ${args.join(' ')} 失败（${result.status ?? result.signal}）${capture ? `\n${result.stderr || result.stdout}` : ''}`);
  }
  return capture ? result.stdout.trim() : undefined;
}

export function npmCli() {
  const candidates = [process.env.npm_execpath];
  for (const executable of [process.execPath, fs.realpathSync(process.execPath)]) {
    candidates.push(path.resolve(path.dirname(executable), 'node_modules/npm/bin/npm-cli.js'));
    candidates.push(path.resolve(path.dirname(executable), '../lib/node_modules/npm/bin/npm-cli.js'));
  }
  const found = candidates.find((p) => p && path.basename(p) === 'npm-cli.js' && fs.existsSync(p));
  if (!found) throw new Error('找不到当前 Node 配套的 npm-cli.js；请按 docs/development.md 配置工具链。');
  return found;
}

export const npm = (args, capture = false) => run(process.execPath, [npmCli(), ...args], capture);
export const node = (script, args = []) => run(process.execPath, [path.join(root, script), ...args]);
export function files() {
  // 检查不依赖 Git 状态，也不读取私密目录或依赖/构建缓存；符号链接不跟随。
  const ignored = new Set(['.git', 'node_modules', '.local', '.cache', 'bin', 'obj', 'dist', 'dist-dev',
    'coverage', 'testresults', 'test-results', 'playwright-report', 'blob-report', 'artifacts', 'publish',
    '.turbo', '.mfsu', '.mfsu-production', '.vite', '.vite-temp', '.vitest', '__pycache__', '.venv']);
  const result = [];
  function visit(directory) {
    for (const entry of fs.readdirSync(directory, {withFileTypes: true})) {
      if (entry.isSymbolicLink()) continue;
      const name = entry.name.toLowerCase();
      if (entry.isDirectory()) {
        if (!ignored.has(name) && !name.startsWith('.umi') && !name.startsWith('dist-')) visit(path.join(directory, entry.name));
      } else if (entry.isFile() && !['.ds_store', 'thumbs.db'].includes(name)) {
        result.push(path.relative(root, path.join(directory, entry.name)).split(path.sep).join('/'));
      }
    }
  }
  visit(root);
  return result.sort();
}

export function scope() {
  const args = process.argv.slice(2);
  if (args.length === 0) return 'all';
  if (args.length === 2 && args[0] === '--scope' && ['docs', 'frontend', 'backend', 'all'].includes(args[1])) return args[1];
  throw new Error('用法：--scope docs|frontend|backend|all（默认 all）');
}

export function main(action) {
  Promise.resolve().then(action).catch((error) => {
    console.error(error.message);
    process.exitCode = 1;
  });
}
