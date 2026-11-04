import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { createRequire } from 'node:module';
import { root, run, main } from './lib.mjs';

// 独立MVC/JSON Schema导出器不调用业务宿主，临时输出留仓库外供复核。
main(async () => {
  const action = process.argv[2] ?? 'check';
  if (!['check', 'generate'].includes(action) || process.argv.length > 3) throw new Error('用法：node scripts/oob-contracts.mjs check|generate');
  const generatorPackage = path.join(root, 'frontend/node_modules/json-schema-to-typescript/package.json');
  if (!fs.existsSync(generatorPackage)) throw new Error('缺少锁定生成器，请先执行 bootstrap。');
  const version = JSON.parse(fs.readFileSync(generatorPackage, 'utf8')).version;
  if (version !== '16.0.0') throw new Error('生成器要求 json-schema-to-typescript 16.0.0，当前 ' + version);
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'oob-contracts-'));
  const routes = path.join(temporary, 'routes.json'), schemas = path.join(temporary, 'schemas.json'), types = path.join(temporary, 'generated.ts');
  run('dotnet', ['run', '--project', 'backend/tools/OobContracts/OobContracts.csproj',
    '--no-launch-profile', '--no-restore', '--', temporary]);
  const schema = JSON.parse(fs.readFileSync(schemas, 'utf8'));
  const names = Object.keys(schema.definitions);
  if (!names.length || !names.includes('JsonElementApiResult')) throw new Error('离线合同缺少正式业务信封');
  // components.schemas只作为现有PC类型访问兼容出口，不是OpenAPI文档。
  const container = {
    ...schema, title: 'OobComponents',
    properties: { schemas: { type: 'object', additionalProperties: false,
      properties: Object.fromEntries(names.map(name => [name, { $ref: '#/definitions/' + name }])), required: names } },
    required: ['schemas'],
  };
  const require = createRequire(path.join(root, 'frontend/package.json'));
  const { compile } = require('json-schema-to-typescript');
  const generated = await compile(container, 'OobComponents', {
    bannerComment: '/** 后端路由与 JSON Schema 同源生成，禁止手改；生成器 json-schema-to-typescript 16.0.0。 */',
    additionalProperties: false, unknownAny: true,
    style: { singleQuote: true, printWidth: 90 },
  });
  if (!generated.includes('export interface OobComponents {')) throw new Error('生成器没有输出预期公开类型');
  fs.writeFileSync(types, generated.replace('export interface OobComponents {', 'export interface components {'));
  // 覆盖迁移中真实出现过的精度、递归、泛型集合和可空信息丢失，验证生成器消费语义。
  const consumerCheck = path.join(temporary, 'consumer-check.ts');
  fs.writeFileSync(consumerCheck, [
    "import type { components as C } from './generated';",
    "const version: C['schemas']['UserDto']['version'] = '9007199254740993';",
    '// @ts-expect-error long不能被生成成number',
    "const invalidVersion: C['schemas']['UserDto']['version'] = 9007199254740993;",
    "const missingReading: C['schemas']['HistoryPointDto']['average'] = null;",
    "const missingCapacity: C['schemas']['HardwareComponent']['capacityBytes'] = null;",
    "const nullableAttribute: C['schemas']['HardwareComponent']['attributes'] = { missing: null };",
    "declare const child: C['schemas']['HardwareComponent']['children'][number];",
    "const recursiveChild: C['schemas']['HardwareComponent'] = child;",
    "declare const item: C['schemas']['ServerDtoPageData']['items'][number];",
    "const requiredItem: C['schemas']['ServerDto'] = item;",
    "const optionalPassword: Pick<C['schemas']['SaveServerRequest'], 'password'> = {};",
    "const power: C['schemas']['PowerAction'] = 'On';",
    '// @ts-expect-error 枚举不得接受未声明字符串',
    "const invalidPower: C['schemas']['PowerAction'] = 'InvalidAction';",
    '// @ts-expect-error 必有但可空的响应字段不能省略',
    "const incompletePoint: C['schemas']['HistoryPointDto'] = { time: '', count: 0 };",
  ].join('\n'));
  const ts = require('typescript');
  const program = ts.createProgram([consumerCheck], { strict: true, noEmit: true, skipLibCheck: true,
    target: ts.ScriptTarget.ES2022, moduleResolution: ts.ModuleResolutionKind.Node10 });
  const diagnostics = ts.getPreEmitDiagnostics(program);
  if (diagnostics.length) throw new Error('生成类型语义验证失败：' + diagnostics.map(diagnostic =>
    ts.flattenDiagnosticMessageText(diagnostic.messageText, '\n')).join('\n'));
  const targets = [
    ['docs/api/oob/routes.json', routes],
    ['docs/api/oob/schemas.json', schemas],
    ['frontend/PC/src/services/oob/generated.ts', types],
  ];
  if (action === 'generate') {
    for (const [target, source] of targets) {
      fs.mkdirSync(path.dirname(path.join(root, target)), { recursive: true });
      fs.writeFileSync(path.join(root, target), fs.readFileSync(source));
    }
    console.log('已同源生成 REST路由、JSON Schema 与PC类型；消费方行为和类型仍须验证。');
  } else {
    const drift = targets.filter(([target, source]) => !fs.existsSync(path.join(root, target))
      || !fs.readFileSync(path.join(root, target)).equals(fs.readFileSync(source)));
    if (drift.length) throw new Error('合同逐字比较失败：' + drift.map(([target]) => target).join('、') + '；请审查后使用 generate，并复验消费者。临时输出：' + temporary);
    console.log('合同检查通过：MVC端点 → JSON Schema → PC TypeScript，固定生成器 ' + version + '，三份正式产物逐字一致。');
  }
  console.log('本次临时导出：' + temporary + '；未删除文件、执行 Git 或访问运行依赖。');
});
