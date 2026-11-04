// 检查现行 Markdown 本地路径；不验证远程 URL、标题锚点或历史正文语义。
import fs from 'node:fs';
import path from 'node:path';
import { root, files, main } from './lib.mjs';

main(() => {
  if (process.argv.length !== 2) throw new Error('check-docs 不接受参数。');
  const markdown = files().filter((f) => /\.md$/i.test(f) && !/^(docs\/archive|reference)\//.test(f));
  const errors = [];
  let links = 0;
  function check(source, line, target) {
    if (!target || target.startsWith('#')) return;
    const fail = (reason) => errors.push(`${source}:${line} ${target}：${reason}`);
    if (/^[A-Za-z]:[\\/]/.test(target) || target.startsWith('\\\\') || /^file:/i.test(target)) return fail('不允许个人绝对路径');
    if (/^[a-z][a-z0-9+.-]*:/i.test(target) || target.startsWith('//')) return;
    links++;
    let decoded;
    try { decoded = decodeURIComponent(target.split(/[?#]/)[0]); }
    catch { return fail('无效 URL 编码'); }
    if (decoded.includes('\\') || path.isAbsolute(decoded)) return fail('请使用仓库相对路径与正斜杠');
    const absolute = path.resolve(root, path.dirname(source), decoded);
    const relative = path.relative(root, absolute);
    if (relative === '..' || relative.startsWith(`..${path.sep}`) || path.isAbsolute(relative)) return fail('链接越出仓库');
    let current = root;
    try {
      for (const part of relative.split(path.sep).filter(Boolean)) {
        if (!fs.readdirSync(current).includes(part)) return fail('目标不存在或大小写不匹配');
        current = path.join(current, part);
      }
    } catch { fail('目标不可读取'); }
  }
  for (const source of markdown) {
    let content;
    try { content = fs.readFileSync(path.join(root, source), 'utf8'); }
    catch { errors.push(`${source}:1 已登记的文档不可读取`); continue; }
    let fence = null;
    content.split(/\r?\n/).forEach((raw, index) => {
      const marker = raw.match(/^\s{0,3}(`{3,}|~{3,})/);
      if (marker) {
        if (!fence) fence = marker[1];
        else if (marker[1][0] === fence[0] && marker[1].length >= fence.length) fence = null;
        return;
      }
      if (fence) return;
      const line = raw.replace(/(`+)([\s\S]*?)\1/g, '');
      for (const match of line.matchAll(/!?\[[^\]]*\]\(\s*(?:<([^>]+)>|([^\s)]+))(?:\s+["'][^"']*["'])?\s*\)/g)) {
        check(source, index + 1, match[1] ?? match[2]);
      }
      const definition = line.match(/^\s{0,3}\[[^\]]+\]:\s*(?:<([^>]+)>|(\S+))/);
      if (definition) check(source, index + 1, definition[1] ?? definition[2]);
    });
  }
  if (errors.length) throw new Error(errors.join('\n'));
  console.log(`文档路径检查通过：${markdown.length} 份现行 Markdown，${links} 个本地链接。`);
});
