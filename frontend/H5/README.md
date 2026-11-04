# H5 移动端（规划中）

本目录保留移动端应用的位置与规划说明，当前**尚未建立应用包、源码或可运行命令**。

## 规划

- 面向手机浏览器/移动端场景，独立技术选型（候选 TypeScript + React + antd-mobile），不与 PC 共享 UI 组件。
- 共享内容限于后端接口合同与生成类型；PC 与 H5 不直接互相导入源码。
- 移动端交互、安全区、触控与响应式布局需单独设计与验收，不通过缩小 PC 页面实现。

## 当前状态

- 无 `package.json`、构建配置或依赖；不提供安装/构建命令。
- 未进行浏览器或真机验收。
- 实现前应先提交 Issue 讨论技术选型与范围，并按 [CONTRIBUTING.md](../../CONTRIBUTING.md) 提交流程。

接口约定见 [docs/api/README.md](../../docs/api/README.md)；桌面与 PC 状态见 [frontend/PC/README.md](../PC/README.md)。
