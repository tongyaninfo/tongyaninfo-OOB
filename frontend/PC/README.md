# PC 管理端

本目录是通研带外系统管理的 PC 管理端，基于 React 19、Umi Max、Ant Design 6 与 TypeScript 构建，支持简体中文、繁體中文与英文。桌面版复用同一套页面与接口层。

## 目录

```text
frontend/PC/
├── config/            # Umi 配置：路由、代理、主题与布局
├── public/            # 静态资源（favicon、图标、品牌图片）
├── src/
│   ├── app.tsx        # 运行时配置（布局、请求、主题）
│   ├── pages/oob/     # 业务页面：总览、服务器、控制台、用户、邮件、异常、MCP
│   ├── pages/         # 其余页面（登录、异常页、账户设置、系统管理）
│   ├── services/oob/  # 与后端 REST 合同对应的接口层与生成类型
│   ├── components/    # 通用组件
│   ├── locales/       # 三语言资源
│   └── utils/         # 格式化与工具函数
├── docs/              # 三语言使用指南
├── tests/             # 测试初始化与夹具
└── package.json       # 独立锁文件工程
```

## 开发

```sh
npm ci --ignore-scripts --no-audit --no-fund
node node_modules/@umijs/max/bin/max.js setup
npm run dev
```

默认打开 `http://127.0.0.1:8000`；`/api/` 经同源代理到本机 API `http://127.0.0.1:5080`。开发产物在 `dist-dev/`，生产构建输出到 `dist/`，两者互不覆盖。

## 检查

```sh
npm run lint          # Biome + tsc
npm test              # Vitest
npm run build         # 生产构建
```

## 接口合同

后端路由与 DTO 是权威源，离线生成到 `docs/api/oob/` 与 `src/services/oob/generated.ts`：

```sh
node ../scripts/oob-contracts.mjs check
node ../scripts/oob-contracts.mjs generate   # 仅在审查后端变更后执行
```

生成文件不手改；接口变更须同步合同、重新生成并验证消费方。

## 模板说明

本工程基于 [Ant Design Pro](https://github.com/ant-design/ant-design-pro) 模板（MIT License，见本目录 [LICENSE](LICENSE)）。模板自带的 dashboard、form、list、profile、result、table-list、chatbot 等演示页面及其专属组件、服务与文案已移除；保留登录、异常页、账户设置、系统管理与 OOB 业务页面。业务功能以 `src/pages/oob/` 为准；品牌、标题、页脚与登录页已替换为本产品信息。第三方素材许可见仓库根 [NOTICE](../../NOTICE)。
