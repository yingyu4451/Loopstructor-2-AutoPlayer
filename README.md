# Loopstructor 2 AutoPlayer

Loopstructor 2 AutoPlayer 是一个面向 Windows x64 测试包的自动游玩工具。管理器负责安装测试载荷、创建隔离的 QA profile、启动游戏和展示状态；BepInEx 插件在游戏进程内读取可验证状态并调用游戏自带的 `GuiGameAutomation.Runtime` 契约完成操作，不占用系统鼠标和键盘。

> 本仓库只包含自动化工具代码，不包含、复制或发布任何游戏 DLL。工具不会改写磁盘上的 `Assembly-CSharp.dll`；它只读取该文件的 SHA-256 以确认游戏构建，并在一次性激活的测试进程中加载 BepInEx 插件。

## 适用范围

- Windows x64、Unity `2022.3.62f3c1`、Mono 后端的 Loopstructor 2 测试包。
- 游戏包必须包含与当前源码一致的 `GuiGameAutomation.Runtime` 公共自动化契约。
- 用于 QA 回归、长时间游玩和异常证据采集，不用于玩家账号、正式存档或线上作弊。
- 当前固定运行时为 BepInEx `5.4.23.5` Windows x64。

未知游戏构建、运行时契约缺失、程序集指纹不符、存档隔离失败或平台写入阻断失败时，自动游玩必须保持待机或进入不兼容状态，不继续操作游戏。

## 从源码构建

要求 Windows PowerShell 5.1 或 PowerShell 7。仓库不依赖机器已安装的 .NET SDK；bootstrap 会把固定的 .NET SDK `8.0.423` 安装到仓库内的 `.dotnet` 目录。

```powershell
Set-ExecutionPolicy -Scope Process Bypass

.\scripts\bootstrap.ps1
.\scripts\build.ps1 -Configuration Release
.\scripts\test.ps1 -Configuration Release -NoRestore -NoBuild
.\scripts\package.ps1 -Version 0.1.1 -SkipBuild
```

若只想一步生成发布包，可以在 bootstrap 后运行：

```powershell
.\scripts\package.ps1 -Version 0.1.1
```

产物位于 `artifacts\release`。详细发布流程见 [docs/release.md](docs/release.md)。

## 使用发布包

1. 解压 `Loopstructor.AutoPlayer-<version>-win-x64.zip` 到单独目录。
2. 启动解压目录根部的 `Loopstructor.AutoPlayer.Manager.exe`。`manager\` 是内部运行时与旧版更新兼容目录，不需要在其中查找或启动程序。
3. 选择打包游戏的 EXE 或游戏根目录。不要选择 Unity 工程目录。
4. 安装或更新测试载荷。管理器只应安装包内 `payload\bepinex` 和 `payload\plugin` 的已知文件。
5. 新建或选择独立 QA profile；不要把正常玩家存档目录配置为测试 profile。
6. 必须从管理器启动本次测试。管理器会为每次启动创建新的 pipe 名称、随机 token、profile 路径、证据路径及期望的 `Assembly-CSharp.dll` SHA-256，并只在该子进程中设置 Skyspine 的 Steam AppID，确保运行的是所选 QA 包而不是 Steam 库中的另一份安装。
7. Manager 会核验插件回传的真实游戏 PID 与可执行文件路径；握手通过后再执行开始、暂停、恢复或停止。直接启动游戏不会携带一次性 `ActivationContext`，插件应保持非活动状态。

每次会话的数据默认位于：

```text
%LOCALAPPDATA%\LoopstructorAutoPlayer\
  profiles\<qa-profile-id>\
  artifacts\<run-id>\
  tickets\launch-<game-root-id>.json
```

启动票据在读取后立即删除，最长有效期为 10 分钟，并绑定具体游戏根目录。pipe 与 token 每次启动重新生成，不应写入日志、提交到 Git 或跨会话复用。

### 运行状态与彻底重启门禁

有效票据激活后，插件会对当前游戏进程中的 BepInEx Manager GameObject 应用 `DontDestroyOnLoad` 和 `HideAndDontSave`，使控制服务跨 Unity 场景切换存活。该保护只存在于本次 QA 进程，不会永久修改玩家的 BepInEx Manager 配置；没有有效票据的普通启动仍保持惰性。

自动化遇到污染状态、连续失败上限、停滞或存档隔离故障时会进入 Faulted，并回传 `NeedsProcessRestart = true`。此时不能在同一游戏进程中开始新一轮：Manager 显示“必须彻底重启”，禁用“开始”并在命令发送层再次拒绝 `start`。必须彻底关闭 Skyspine，再由 Manager 创建新票据并重新启动。开发截图可使用 `--demo-restart-required` 复现此门禁；该参数隐含 demo 模式，不连接真实游戏。

安全握手通过后的运行按钮矩阵如下；“安装、启动测试包、更新”等管理按钮不属于此矩阵：

| 状态 | 开始 | 暂停 | 继续 | 停止 |
|---|---:|---:|---:|---:|
| 握手未通过 | 禁用 | 禁用 | 禁用 | 禁用 |
| Standby / Completed，且无需重启 | 启用 | 禁用 | 禁用 | 禁用 |
| Running | 禁用 | 启用 | 禁用 | 启用 |
| Paused | 禁用 | 禁用 | 启用 | 启用 |
| Faulted 或 `NeedsProcessRestart = true` | 禁用 | 禁用 | 禁用 | 禁用 |

## 安全边界

- QA 存档通过游戏的 `SavePathUtility.GetCompanyAppDataPath` 契约重定向到独立 profile。
- 平台写入门禁是强制条件，不提供关闭选项。激活会话必须同时成功阻断 Steam 成就、IGP 成就、结算飞书自动上传和 `RestartAppIfNecessary` 四个已知入口，否则构建被标记为不兼容且不能开始自动游玩。
- 游戏诊断产物必须重定向到本次 artifact；重定向失败同样拒绝开始。
- 开局默认防线若明确报告 `prepared=false`、`statePolluted=false` 且不要求 reset，会视为初始化暂态并在干净状态下重试，不累计普通连续失败；返回结果任意嵌套层出现 `statePolluted=true`、`needsReset=true`，或证明动力站点放置已经提交但后续步骤失败，都会按不安全状态处理，停止并要求新进程。
- 前端写操作会等待游戏的全局模块与当前场景 Main 完成初始化，并要求就绪状态稳定一个轮询周期。进入对局后，合法路线与子关卡选择优先于默认防线，避免在随机模式路线图背后提前放置道具或绘线。
- “继续隔离档中的未完成对局”只适用于 QA profile。成功执行 `continueGame` 后不会重新绘制开局默认防线，避免破坏存档中已有轨道和车辆。
- Harmony 补丁只存在于当前游戏进程内；退出游戏后失效。
- Manager 设置的 `SteamAppId`/`SteamGameId` 只由本次游戏子进程继承，不写入 `steam_appid.txt`，也不修改系统级环境变量。
- 自动玩家通过游戏运行时契约操作，不发送系统级鼠标或键盘输入。
- 连续失败、长时间无进展或兼容性检查失败会停止自动操作并保留状态、日志和截图证据。

平台门禁只覆盖当前已识别的写入入口，不能承诺测试账号“零痕迹”。Steam/IGP 会话、在线状态、游戏时长、Overlay、云同步或新版新增的统计/遥测入口仍可能留下平台侧记录；涉及正式账号时应改用专用 QA 账号、离线环境或无平台测试包。

完整约束、限制和卸载注意事项见 [docs/safety.md](docs/safety.md)。

## 当前真实包验证

2026-07-29 在 Windows x64 的 Skyspine `1.385` 构建上完成了真实打包游戏验证：

- 普通模式与随机模式都达到 `PLAY_OK`：两次独立运行均观察到 2 波启动、1 波完成，完成波后奖励物收集与奖励选择、波后路线选择，并进入第 2 波；状态证据均为 `WavesStarted=2`、`WavesCompleted=1`。
- QA 存档隔离、平台写入门禁和游戏诊断产物重定向均为 true。真实玩家存档目录中原有 4 个文件的哈希与时间未改变。
- 测试前后 `Assembly-CSharp.dll` SHA-256 均为 `962b9f69774d4ea20458877363bc33bc7f85cfa207baa1d775b0ea5677140f29`，即 `assemblyUnchanged=true`。
- 首次随机模式测试曾因过早输入在 `Temp` 场景触发 `MetroTDAffixCreatorHandler.Clear()` 空引用；加入只读加载门禁后不再复现，并完成上述跨波验证。游戏仍会在随机转盘离场后记录 `RandomMode_TurnTableManager.StopDecorateAnimation` 的非致命 `Animator.Play` 空引用，当前未屏蔽，便于游戏侧继续修复。
- 本机没有生产 Steam AppID `3841840` 的许可，日志出现 `[Steamworks.NET] SteamAPI_Init() failed`。两种模式的隔离自动测试仍可继续，但该结果不表示 Steam 功能已被禁用，也不能外推到有许可的 QA 账号。

## 项目结构

```text
src/Loopstructor.AutoPlayer.Core/      共享协议、状态模型与决策引擎
src/Loopstructor.AutoPlayer.Plugin/    netstandard2.1 BepInEx 5 插件
src/Loopstructor.AutoPlayer.Launcher/  根目录单文件启动器
src/Loopstructor.AutoPlayer.Manager/   .NET 8 Windows 管理器
src/Loopstructor.AutoPlayer.Updater/   独立更新进程
tests/                                 自动化测试
scripts/                               bootstrap、构建、测试和打包脚本
docs/                                  架构、安全与发布说明
```

架构和数据流见 [docs/architecture.md](docs/architecture.md)。

## GitHub 与自动更新

push 和 pull request 会运行构建与测试；推送 `v*` tag 会生成 Windows x64 zip、SHA-256 和 `autoplayer-update-manifest.json`，随后发布 GitHub Release。更新器只从同一个 Release 获取清单指定的资产，并在替换前校验文件大小、SHA-256 和包内 `autoplayer-release.json`。GitHub Actions artifact 下载时仍由平台套一层 ZIP，但打开后直接是可运行目录和根部 Manager EXE，不再包含第二层产品 ZIP。

默认发布与更新源为 [`yingyu4451/gui2`](https://github.com/yingyu4451/gui2)。Manager 设置可以切换到其他仓库；环境变量 `LOOPSTRUCTOR_AUTOPLAYER_GITHUB_OWNER` 和 `LOOPSTRUCTOR_AUTOPLAYER_GITHUB_REPOSITORY` 的优先级更高，适合临时测试 fork。

公开仓库可匿名检查 GitHub Releases。私有仓库必须在启动 Manager 的进程环境中提供只读 `LOOPSTRUCTOR_AUTOPLAYER_GITHUB_TOKEN`；不要把 token 写入源码、Manager 设置、发布包或日志。
