# Codex 换机交接

本文是 Loopstructor 2 QA Tool 在更换电脑或重新建立 Codex 项目任务时的唯一交接入口。它只保存会影响后续开发决策的项目事实；具体功能、协议和历史版本仍以仓库源码及对应专题文档为准。

## 当前快照

快照日期：2026-09-01（Asia/Shanghai）。

| 项目 | 当前值 |
|---|---|
| GitHub 远端 | `https://github.com/yingyu4451/Loopstructor-2-QA-Tool.git` |
| 分支 | `main` |
| 产品版本 | `0.6.56` |
| 产品提交 | `470305cb4e25090d2718dda81563e061926163c7` |
| 发布标签 | `v0.6.56`，已推送并指向上述产品提交 |
| 工作区 | 生成本快照前为干净状态，`main` 与 `origin/main` 一致 |
| 交接文档提交 | 本文件所在的后续纯文档提交；不要把它误认为 `v0.6.56` 产品提交 |

`0.6.56` 修复了更新窗口在紧凑尺寸下被裁切，以及 Electron 更新窗口占用正式安装目录、导致更新替换失败的问题。更新窗口现在先把完整 Electron 运行时复制到 `%TEMP%\LoopstructorAutoPlayerUpdater\electron-*`，再从临时副本显示进度并托管 .NET 更新事务。

本地最终验证：

- `scripts/test.ps1 -Configuration Release -NoRestore -NoBuild`：.NET `597/597`、Updater verification、TypeScript、ESLint 全部通过；Vitest `6` 个文件、`14/14`；Electron 主进程迁移测试 `1/1`。
- `pnpm test:e2e`：`2/2`，覆盖统一桌面全部路由、renderer sandbox，以及 `680×520` 和 `760×600` 更新窗口无横纵向裁切。
- 完整包 `Loopstructor.AutoPlayer-0.6.56-win-x64.zip`：`221998481` 字节，SHA-256 `81e1968dca3a468800194cbcf8a53846a344ee76028658c018dab302b9a11fa7`。
- `0.6.55 → 0.6.56` 增量包：`110177220` 字节，SHA-256 `6fc4e5531df18a9d01dc4a27a13cd434b451dbc17a860deb4b4a5fa87510e8a4`。
- 完整包、增量包与 `autoplayer-update-manifest.json` 已在本地逐项核对。标签已触发 GitHub Release workflow；换机后应在 [v0.6.56 页面](https://github.com/yingyu4451/Loopstructor-2-QA-Tool/releases/tag/v0.6.56) 再确认线上资产和校验文件均已发布。

## 开工前的事实来源

按以下顺序读取，遇到冲突时以前者和当前源码为准：

1. [`AGENTS.md`](../AGENTS.md)：版本、提交、推送和标签的强制工作流。
2. [`README.md`](../README.md)：产品能力、构建入口、使用方式和当前真实包验证。
3. [`docs/architecture.md`](architecture.md)：组件边界、IPC、运行时契约、自动游玩与作弊数据流。
4. [`docs/safety.md`](safety.md)：玩家模式、隔离 QA、写事务、证据与隐私边界。
5. [`docs/release.md`](release.md)：固定依赖、发布流程与逐版本变更。
6. 本文：换机所需的项目脉络、非仓库数据和 Codex 恢复清单。

Codex 聊天记录不是事实来源。旧记录如果与当前源码、测试或上述文档冲突，应丢弃旧结论，而不是复原旧实现。

## 仍然有效的项目决策

- 产品面向 Windows x64 的 Unity `2022.3.62f3c1` Mono 打包游戏；固定使用 BepInEx `5.4.23.5`。当前注入组合要求游戏完整路径只含 ASCII 字符，可包含英文、数字和空格。
- Electron 44 + Vue 3 + TypeScript 是当前统一桌面 UI；`.NET 8 Host` 负责安装、进程绑定、可信会话、存档、自动游玩、作弊与更新交接；BepInEx 插件只在游戏进程内执行运行时操作。
- 根 Launcher 是 NativeAOT 单文件入口；无窗口 .NET Updater 是隐藏事务层。旧 WPF Manager 窗口与测试已删除，`src/Loopstructor.AutoPlayer.Manager` 只保留 Host 复用的服务和模型。
- 仓库不携带游戏 DLL，不修改磁盘上的 `Assembly-CSharp.dll`。插件通过反射连接游戏自带的 `GuiGameAutomation.Runtime`，未知程序集指纹或契约缺失时必须保持待机或拒绝运行。
- 玩家常驻模式使用当前玩家存档和原平台行为；隔离 QA 模式使用一次性票据、独立 profile、平台写入门禁和诊断重定向。两种模式的门禁不能混用。
- 自动玩家不发送系统鼠标或键盘输入。所有 Unity 写操作都进入主线程，并使用请求 ID、进程实例、身份锁定事务和只读对账避免不确定写入被重复执行。
- Electron renderer 保持 `sandbox`、`contextIsolation` 和严格 IPC 白名单；UI 沿用机械齿轨视觉、离线 Iconify 图标和只在真实溢出时出现滚动条的布局原则。
- `0.6.55` 新增独立存档页：枚举受管快照，确认后关闭游戏，以可回滚事务恢复选定存档，再重新启动游戏。
- `0.6.56` 新增 Electron 更新运行时迁移：可见更新窗口不能继续从待替换的正式 `manager` 目录运行。
- 产品版本与协议版本分开管理。修改插件或随包 Manager、Updater、Launcher 时按 [`AGENTS.md`](../AGENTS.md) 递增产品 patch；协议、目录格式和 manifest schema 只在接口确实变化时调整。

### 已淘汰的旧记录

早期 Codex 任务曾直接修改独立的 `steam-plugin`，使用 `1.0.x` 版本、F8 热键和直接部署 DLL。那是当前仓库形成前的历史原型，不是现行产品架构，也不是迁移来源。不要把它的版本号、部署步骤、运行日志或策略文件复制回本仓库。

旧任务中出现过 `j-space`，但当前项目和换机流程不依赖它；新电脑无需为继续本仓库而安装该 skill。

## 已知限制与待复验项

- 自动游玩页仍明确标注“尚未完成”。可以测试，但不要用于无法回滚的重要存档；玩家模式的重要存档应先备份。
- 当前工具是进程内灰盒自动化，不能替代真实鼠标、键盘、手柄、窗口焦点、平台 Overlay 和无注入正式包的独立黑盒冒烟测试。
- 随机模式转盘离场后，游戏仍可能从 `RandomMode_TurnTableManager.StopDecorateAnimation` 的延迟回调记录非致命 `Animator.Play` 空引用。工具不会用 Harmony 隐藏这个游戏侧问题。
- 旧电脑/账号没有 Steam AppID `3841840` 的许可，`SteamAPI_Init` 相关结果不能外推到有许可 QA 环境。正式平台验证应使用专用 QA 账号、离线环境或无平台测试包。
- QA 平台门禁只覆盖已知写入入口，不等于“零平台痕迹”；在线状态、时长、Overlay、云同步或新增 SDK 行为仍可能留下记录。
- 换机后要重新检查 `v0.6.56` GitHub Release 的完整包、增量包、manifest 和 SHA-256 是否已由异步 workflow 发布成功。

## 新电脑迁移清单

### 1. 仓库与构建工具

安装 Git、Windows PowerShell 5.1 或 PowerShell 7、Node.js 24 和 pnpm 11。仓库的 `packageManager` 当前固定为 `pnpm@11.19.0`；.NET SDK 不必预装，由 bootstrap 在仓库的 `.dotnet` 中安装固定的 `8.0.423`。

```powershell
git clone https://github.com/yingyu4451/Loopstructor-2-QA-Tool.git
Set-Location .\Loopstructor-2-QA-Tool
git fetch --tags origin
git checkout main
git pull --ff-only origin main
git status --short --branch
git show --no-patch --decorate v0.6.56

Set-ExecutionPolicy -Scope Process Bypass
.\scripts\bootstrap.ps1
.\scripts\build.ps1 -Configuration Release
.\scripts\test.ps1 -Configuration Release -NoRestore -NoBuild
```

预期版本面均为 `0.6.71`：

- `Directory.Build.props` 的 `VersionPrefix`；
- `src/Loopstructor.AutoPlayer.Plugin/PluginInfo.cs`；
- `desktop/package.json`；

`.dotnet`、`.tools`、`desktop/node_modules`、`artifacts` 等目录可在新电脑重新生成，不作为 Git 迁移内容。

### 2. Codex 设置、skills 与插件

在新电脑重新安装 Codex 并重新登录。个人设置只按需要手工恢复，可参照官方 [Codex 配置参考](https://learn.chatgpt.com/docs/config-file/config-reference)；不要整份复制旧电脑的 `config.toml`，其中的绝对路径、MCP 命令、权限和环境变量可能不再适用。

旧电脑上需要记录的自定义 skills：

- `find-skills`
- `frontend-design`
- `grill-me`

这些 skill 位于旧电脑的用户级 skill 目录，应从原始来源重新安装，或把经过检查的 skill 文件夹作为独立私有备份迁移。Skill 结构和安装原则见官方 [Build skills](https://learn.chatgpt.com/docs/build-skills)。项目事实不放入个人 skill；本文件就是它们的仓库内替代品。

曾启用的 Codex 插件包括 Browser、Computer Use、Documents、PDF、Presentations、Spreadsheets、Template Creator、Visualize、Game Studio、GitHub 与 Codex App Tools。应在 Codex 插件目录中重新安装并重新授权需要的项目，不要复制 `.codex/plugins/cache`。插件和外部连接的可用性以新电脑当前 Codex 版本及账号权限为准。

### 3. 不在仓库中的项目数据

- Unity 游戏工程必须单独迁移。旧电脑曾使用 `D:\Unity Project\Loopstructor2`，但新路径可以不同；本工具仓库不包含游戏源码。迁移后在 Manager 中重新选择工程并安装 Editor 连接组件，不要手工复制旧的 `Editor/Managed`。
- 打包测试游戏也要单独迁移，并放在完整路径只含 ASCII 字符的位置。Manager 中应选择打包游戏 EXE 或根目录，不能选择 Unity 工程目录。
- 仓库根的 `artifacts` 被 Git 忽略。只有确实需要保留的失败日志、截图、校验记录和测试报告才单独复制，并在分享前检查账号名、绝对路径和未公开内容。
- 玩家备份默认位于 `%LOCALAPPDATA%\LoopstructorAutoPlayer\save-backups`。需要保留玩家读档点时只复制该目录，并在新电脑首次使用前再次备份。
- `profiles` 是隔离 QA 数据，原则上应新建；只有为了复现实验才有选择地迁移。不要把旧 profile 当作玩家正式存档。
- 不迁移 `%LOCALAPPDATA%\LoopstructorAutoPlayer\control`、`editor-instances` 或 `tickets`。它们绑定旧电脑的用户、进程、目录、程序集指纹和高熵令牌；新电脑应由 Manager 或 Editor Bridge 重新生成。

### 4. 绝不能提交或普通分享的数据

以下内容不得进入本仓库，也不要通过普通聊天、Issue 或日志发送：

- `.codex/auth.json`、认证备份、访问令牌、API key 和操作系统凭据；
- `.codex/.sandbox-secrets`、MCP/connector 密钥及包含密钥的环境变量；
- Codex 会话、记忆、日志、队列和状态 SQLite/JSONL 数据库；
- 未审查的完整 `config.toml`；
- AutoPlayer 的 control token、启动票据和任何仍有效的本机授权文件；
- 含玩家账号、个人路径或未公开游戏内容的原始 artifact。

官方文档明确将 `~/.codex/auth.json` 视同密码。换机优先重新登录，不要把认证文件放进 Git、网盘共享目录或本交接文档。

## 新电脑的第一条 Codex 提示词

克隆仓库后，在仓库根目录创建新任务并发送：

```text
请接续 Loopstructor 2 QA Tool 项目。开始工作前：

1. 完整读取 AGENTS.md 和 docs/codex-handoff.md。
2. 运行 git status --short --branch、git remote -v、git log -5 --decorate --oneline，确认当前分支、远端和工作区；保留任何已有未提交修改。
3. 核对 Directory.Build.props、PluginInfo.cs、desktop/package.json 与最新产品标签的版本是否一致。
4. 按当前任务只读取相关的 README、docs/architecture.md、docs/safety.md 或 docs/release.md；以源码、测试和仓库文档为准，不假设旧电脑的 Codex 聊天仍然存在。
5. 修改插件或随包 Manager、Updater、Launcher 时严格执行 AGENTS.md 的 patch 版本、验证、提交、推送和标签规则；纯文档、诊断或测试修改不要擅自递增产品版本。
6. 不恢复旧 steam-plugin/F8/1.0.x 原型，也不要复制旧电脑的 control、tickets、Codex 凭据或缓存。

我的本次任务是：<在这里写新需求>
```

## 维护本文

只有在产品架构、固定工具链、迁移数据、关键安全边界、当前发布基线或已知复验项发生变化时更新本文。不要加入临时任务 ID、完整聊天复述、一次性日志、令牌或个人无关事项。

纯交接文档修改不递增产品版本、不创建发布标签；若同一任务还修改随包产品内容，则仍按 [`AGENTS.md`](../AGENTS.md) 执行产品发布流程。
