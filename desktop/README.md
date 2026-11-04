# 本地桌面版

Electron 管理本地 .NET 10 自包含宿主，复用现有 [PC](../frontend/PC/README.md)、Controller、业务服务与带外驱动；桌面仅使用 SQLite，原服务端仍使用 PostgreSQL/TimescaleDB。选择依据见 [ADR-0011](../docs/adr/ADR-0011-本地桌面宿主与SQLite存储.md)。

共享 PC 新增的 MCP 页面在桌面显示“不支持外部 MCP 服务”；宿主强制 `Mcp:Supported=false`，不对外开放 Electron 动态回环端口、不绕过桌面 Gate，LAN 介质端口继续只提供获准介质内容。共享 MCP 业务实体仍进入 SQLite 的独立增量迁移并遵守显式升级流程；OAuth 外部服务本期不在桌面启用。Swagger/OpenAPI 服务及 UI 在桌面同样退役，REST 类型继续来自独立离线 JSON Schema 合同。详细范围见[ADR-0012](../docs/adr/ADR-0012-MCP服务授权与离线合同.md)与[MCP说明](../docs/api/mcp/README.md)。

## 目录与数据所有权

- `src/main`：单实例、窗口、托盘、受限导航、子进程握手与退出。
- `src/preload`：仅白名单桌面能力，不把 Node.js 或任意文件访问交给页面。
- `backend/OobManagement.Desktop.Host`：loopback 同源宿主、首启向导、备份和显式升级。
- `backend/OobManagement.Desktop.Storage`：独立 SQLite EF 迁移、UTC 微秒、JSON TEXT、账号写锁和历史时间桶。
- `tests`：真实 SQLite/HTTP、恢复、并发及 Electron 进程协议测试。

普通运行不需要 Node.js、.NET SDK、Docker 或外置数据库；Linux 仍需满足目标发行版的原生运行库基线。运行数据位于 Electron 的每用户 `userData`，可通过绝对路径环境变量 `OOB_DESKTOP_DATA` 指定独立目录。程序与数据分开，升级或卸载程序保留用户数据。

```text
config/keys/       凭据及会话的 Data Protection 密钥
config/           可选私有 application.json
data/oob.sqlite    数据库及运行中的 WAL/SHM
exception-spool/   故障缓冲
virtual-media/     镜像、介质授权和意图记录
backups/           一致性数据库快照与备份包
```

安装后首次显示初始化向导，明确设置 `admin` 密码后才创建迁移结构和管理员；已有数据升级需要有效管理员密码，先备份再迁移。普通启动不调用 `Migrate` 或 `EnsureCreated`，未就绪时不启动采集。电源全局开关继续默认关闭，真实电源操作须由使用者在明确确认目标服务器、动作与时机后启用。

桌面首次进入默认简体中文；初始化及升级页面支持简体中文、繁體中文和English，和登录页共用当前来源的语言选择。切换不刷新或清空已输入密码，详见语言修正。

## 开发、检查和打包

仓库根已有工具链要求见[开发手册](../docs/development.md)。依赖只安装到项目本地，保留 PC 原锁和桌面独立锁：

```sh
node scripts/bootstrap.mjs
node desktop/node_modules/electron/install.js
node scripts/verify.mjs --scope all
npm --prefix desktop run start
```

`bootstrap` 忽略安装生命周期，Electron 运行时通过第二条显式下载；不执行 Git 或数据库初始化。`start` 构建 PC、发布当前平台自包含宿主并启动 Electron，使用新的仓库外 staging。

```sh
npm --prefix desktop run package -- --platform darwin --arch arm64
npm --prefix desktop run package -- --platform darwin --arch x64
npm --prefix desktop run package -- --platform win32 --arch x64
npm --prefix desktop run package -- --platform linux --arch x64
npm --prefix desktop run package -- --platform linux --arch arm64
```

只生成本次需要的 Windows EXE 和 Linux AppImage：

```sh
npm --prefix desktop run package -- --platform win32 --arch x64 --format nsis
npm --prefix desktop run package -- --platform linux --arch x64 --format AppImage
```

当前锁定版本可在 macOS ARM64（含 Rosetta）完成上述两种 x64 格式的交叉打包；NSIS 和 SquashFS 工具使用校验过的用户缓存，不需全局安装或新增虚拟机。打包成功与包结构/校验通过不等于已经在 Windows/Linux 实际安装启动。

打包生成 `.dmg`/`.zip`、Windows NSIS `.exe`、Linux `.AppImage`/`.deb`/`.rpm`，具体目标由配置指定；推荐在相应操作系统构建和验证。`--dir` 只产出应用目录；`--skip-frontend-build` 复用最近一次已成功构建的 PC `dist`。输出默认在仓库外新目录，最后一行报告路径；可显式设置 `OOB_DESKTOP_OUTPUT`。命令只产出本地文件，不自动发布。正式分发的签名、公证和目标操作系统人工验收分别记录，未签名本地产物不代表已完成正式发布。

桌面构建将 `resources/offline/manifest.json` 中登记的固定模板图片与字体校验 SHA256/大小后复制到发布副本，再改写副本引用与默认头像；原 PC 源码、原 `dist` 与服务端配置不变。模板增加未登记 CDN 资源时阻断打包。产物路径守卫拒绝私有配置、凭据目录和 SQLite 运行数据进入安装包。macOS 默认本地 ad-hoc 签名，并用系统 `hdiutil` 生成和校验 DMG，不读取用户签名身份或额外使用 Python 打包工具。

Linux 当前使用锁版工具的 FUSE2 AppImage，目标桌面系统需满足 `libfuse.so.2` 及 Electron/.NET 原生运行库要求；AppImage 为可执行的便携包。若复制后没有执行权限，先运行 `chmod +x OutOfBandManagement-1.0.0-linux-x86_64.AppImage`，再启动。配置取消工具默认的无条件 `--no-sandbox`；工具的 AppRun 在检测不到 user namespaces 时仍有条件降级，不将其表述为所有 Linux 环境均启用 Chromium OS 沙箱。

```sh
dotnet test desktop/tests/OobManagement.Desktop.Tests/OobManagement.Desktop.Tests.csproj
npm --prefix desktop test
npm --prefix desktop run smoke -- --host /absolute/path/to/OobManagement.Desktop.Host
npm --prefix desktop run smoke -- --application /absolute/path/to/OutOfBandManagement.app/Contents/MacOS/OutOfBandManagement
```

冒烟使用独立临时数据目录；截图与运行证据放仓库外。测试使用模拟带外驱动，不因页面挂载或自动验证执行真机写入。

## 关闭、休眠和恢复

有托盘时关窗隐藏但保留 WebContents/.NET，继续采样与本地 ISO 会话；没有托盘时关窗正常退出。显式退出先结束页面，再通过标准输入关闭自有宿主。父进程管道消失也会停止宿主；独占数据目录租约防止绕过 Electron 同时启动两个采集器。休眠和退出期间不采样，恢复后保留真实缺口；不自动重发电源、UID或介质意图。

控制台焦点或指针捕获期间，组合键交给页面，离开后恢复普通菜单；退出前复用页面既有 blur 按键释放链并有界等待，再销毁窗口。此浏览器调度检查不能替代真机输入和固件释放验收。

菜单“数据维护”提供管理员备份下载，备份包含数据库、配置和解密密钥、介质文件及意图。备份含敏感数据，应存放在受保护位置。SQLite 使用在线备份包含已提交 WAL，不能只复制运行中的主文件。

备份排除介质进程租约和异常重放游标；恢复异常从头按稳定 ID 幂等补入，避免较新游标领先于数据库快照而遗漏日志。

先退出程序，再用对应平台宿主恢复到**新的空目录**：

```sh
/absolute/path/to/OobManagement.Desktop.Host --desktop-data /absolute/path/to/new-profile --restore-backup /absolute/path/to/OutOfBandManagement-backup.zip
```

Windows 使用相应 `.exe`。恢复拒绝路径越界、重复文件和覆盖已有配置/数据库，恢复后检查数据库完整性；失败保留现场，不自动清理或覆盖。随后用 `OOB_DESKTOP_DATA` 指向新目录启动 Electron。跨机器恢复还需核实本机 Data Protection 密钥保护与访问权限，不能只复制数据库推定设备密码可解密。

## 镜像库局域网供给

控制台本地 ISO 直接映射和外部 URL 挂载可复用；镜像库挂载需要 BMC 主动下载桌面电脑上的文件。业务 UI/API 始终只监听 `127.0.0.1`，不以开放整个业务接口解决镜像下载。

可在本机 `config/application.json` 显式配置以下**占位示例**后重启：

```json
{
  "Desktop": {
    "MediaListenAddress": "192.0.2.10",
    "MediaPort": 18081,
    "MediaAllowedAddresses": ["192.0.2.20"]
  }
}
```

默认不启动此端点。启用时必须是本机具体 IPv4、非特权端口和明确 BMC IP 白名单；需要现场核对网卡、路由及防火墙。端点仅允许 GET/HEAD 的镜像内容路径，继续校验原介质授权，登录、API、页面、WebSocket 均不开放。该端点使用可信管理局域网内的 HTTP 文件供给；未启用时镜像库挂载明确不可用，不影响控制台直接映射或外部 URL。

## 验收边界

真实 SQLite 和原 PostgreSQL Testing 检查、Electron 浏览器验证、三平台打包、两类真机控制台、ISO及人工电源验收分别报告。配置支持某平台不表示该平台已经实测；构建/模拟测试不代替真机业务验收。当前执行结果见交付时仓库外证据，本文不复制历史测试数量。
