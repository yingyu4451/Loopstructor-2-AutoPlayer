# Loopstructor 2 AutoPlayer

Loopstructor 2 AutoPlayer 是一个面向 Windows x64 打包游戏的自动游玩与本地调试工具。Manager 安装并验证载荷、连接已经运行或由它启动的游戏，并展示自动游玩状态；BepInEx 插件在游戏进程内读取可验证状态并调用游戏自带的 `GuiGameAutomation.Runtime` 契约完成操作，不占用系统鼠标和键盘。玩家模式安装后在后台待命，可在游戏运行期间随时从 Manager 开始、暂停或停止自动游玩。

> 本仓库只包含自动化工具代码，不包含、复制或发布任何游戏 DLL。工具不会改写磁盘上的 `Assembly-CSharp.dll`；它只读取该文件的 SHA-256 以确认游戏构建。玩家模式使用当前 Windows 用户下、绑定游戏目录与程序集指纹的本机控制注册；隔离 QA 模式仍使用一次性激活上下文。

## 适用范围

- Windows x64、Unity `2022.3.62f3c1`、Mono 后端的 Loopstructor 2 测试包。
- 游戏包必须包含与当前源码一致的 `GuiGameAutomation.Runtime` 公共自动化契约。
- 当前 BepInEx 与该 Unity Mono 构建组合要求完整游戏路径只含 ASCII 字符；可包含英文字母、数字和空格。该限制来自注入后的运行时兼容性，未安装 BepInEx 的游戏本体不受影响。
- 玩家模式用于本地单机游玩，直接使用当前玩家存档和平台行为；自动游玩或作弊造成的存档变化不会自动回滚。需要可重复、可审计且不接触正常存档的回归时，应使用隔离 QA 模式和专用测试账号。
- 当前固定运行时为 BepInEx `5.4.23.5` Windows x64。

未知游戏构建、运行时契约缺失或程序集指纹不符时，自动游玩必须保持待机或进入不兼容状态。隔离 QA 模式还会把存档隔离、平台写入阻断和诊断产物重定向作为强制门禁；玩家模式反而要求这些 QA 重定向均未启用。

## 从源码构建

要求 Windows PowerShell 5.1 或 PowerShell 7。仓库不依赖机器已安装的 .NET SDK；bootstrap 会把固定的 .NET SDK `8.0.423` 安装到仓库内的 `.dotnet` 目录。

```powershell
Set-ExecutionPolicy -Scope Process Bypass

.\scripts\bootstrap.ps1
.\scripts\build.ps1 -Configuration Release
.\scripts\test.ps1 -Configuration Release -NoRestore -NoBuild
.\scripts\package.ps1 -Version 0.5.1 -SkipBuild
```

若只想一步生成发布包，可以在 bootstrap 后运行：

```powershell
.\scripts\package.ps1 -Version 0.5.1
```

产物位于 `artifacts\release`。详细发布流程见 [docs/release.md](docs/release.md)。

## 使用发布包

1. 将 `Loopstructor.AutoPlayer-0.5.1-win-x64.zip` 完整解压；压缩包内只有一个固定的 `Loopstructor 2.AutoPlayer\` 顶层目录，不在目录名中附加版本号。不要直接在资源管理器的 ZIP 预览中运行程序。
2. 进入该目录并启动根部的 `Loopstructor.AutoPlayer.Manager.exe`。发布包已自带唯一一套共享 .NET/WPF 运行时，无需安装系统 .NET；内部 Manager 和 Updater 均位于 `manager\` 目录。用户不需要进入内部目录查找或启动程序。
3. 选择打包游戏的 EXE 或游戏根目录。不要选择 Unity 工程目录。Manager 会在安装前拒绝包含中文或其他非 ASCII 字符的完整游戏路径，并给出移动目录的中文提示。
4. 安装或更新测试载荷。管理器只应安装包内 `payload\bepinex` 和 `payload\plugin` 的已知文件。
5. 安装完成后，Manager 会为该游戏目录创建当前 Windows 用户专用的玩家模式本机控制注册；注册绑定游戏根目录、插件协议和 `Assembly-CSharp.dll` SHA-256，不会开放网络端口。
6. 可以先手动启动游戏，也可以点击 Manager 的“启动游戏”。游戏已运行时 Manager 只连接现有进程，不会重复启动。
7. Manager 会核验插件回传的真实游戏 PID、可执行文件路径、程序集指纹、运行时契约和本机令牌。握手通过后可随时开始、暂停、恢复或停止自动游玩。

每次会话的数据默认位于：

```text
%LOCALAPPDATA%\LoopstructorAutoPlayer\
  control\installed-<game-root-id>.json
  profiles\<game-id>\<qa-profile-id>\
  artifacts\<game-id>\<run-id>\
  tickets\launch-<game-root-id>.json
```

玩家模式的本机注册使用稳定的 pipe 基础名与高熵 token，使手动启动的游戏也能被同一用户的 Manager 找到；每个实际游戏进程使用带 PID 的专属端点，握手后每条请求还必须匹配随机进程实例标识。插件和 Manager 同时校验目录、PID、进程启动时间、进程实例、程序集指纹及运行时契约；检测到多个未绑定的同目录游戏进程时会拒绝任意选择。隔离 QA 模式的启动票据在读取后立即删除，最长有效期为 10 分钟，并为每次启动重新生成 pipe 与 token。两类 token 都不得写入日志或提交到 Git。

### 作弊调试模式

通过安全握手的游戏进程都可以随时打开独立的“作弊工具”窗口，再手动开启或关闭作弊模式；不需要启动前选择单独的作弊调试会话。作弊工具与 Manager 是两个独立任务栏窗口，最小化 Manager 不会同时最小化作弊工具，操作作弊工具也不会把 Manager 抢到前台。作弊工具与自动游玩不能同时运行，关闭作弊模式后可立即重新开始自动游玩。玩家模式会直接修改当前玩家存档，使用前应自行备份；隔离 QA 模式才把修改限制在可丢弃测试档。

战车、附魔、消耗品、遗物和怪物目录优先显示游戏配置中的简体中文名称及图标，同时保留内部 ID 便于诊断；配置缺少显示资料时才回退到内部 ID 和占位图。插件不会为了读取中文名而切换游戏的全局语言。

作弊工具提供以下能力：

1. 获取指定战车，并可同时选择多种附魔及各自等级；
2. 获取指定消耗品；
3. 获取或删除背包中的普通弹射点、能量弹射点，并可单独或全部删除场上弹射点；
4. 开启或关闭基地无敌；
5. 结束当前波次；
6. 清除当前已经生成的所有敌人；
7. 删除指定已有战车；使用中文属性名修改车辆属性，并编辑已有战车的附魔等级或移除附魔；
8. 修改指定敌人的属性；对象列表同时显示中文名、枚举名和图标，战车还显示已有附魔图标；
9. 在游戏画面中显示敌人运行时 ID，便于查询和修改；
10. 获得或删除指定遗物；
11. 在一个或多个指定坐标周围分散生成允许且可被战车攻击的怪物；默认使用当前波次的正式 AI 等级和难度倍率，也可显式覆盖等级。进入定位状态后可在游戏内反复按住左 Alt 并点击鼠标左键添加位置，游戏画面会显示各点编号与坐标，作弊工具可单删或清空点位；
12. 开启地图跳关后，可在当前地图界面点击该阶段内已通过、当前或未来节点直接跳转；波次未完全结算、子关卡待选、陈旧阶段请求、跨阶段目标或失效节点都会拒绝。

作弊工具中的所有下拉选择器均可搜索；资源、运行时对象和属性会同时显示中文名与枚举名，并可按中文名、枚举名、运行时 ID 或内部 ID 筛选。选择器获得焦点后结果列表保持打开，直到确认选择或主动关闭；搜索文本本身不会直接作为命令参数。

仅获得作弊能力不会阻止自动游玩；实际开启作弊模式后必须先关闭它才能启动自动游玩。任何会改变游戏状态的作弊操作都会先写入本机持久作弊标记，再调用游戏 API；标记失败时写操作直接拒绝。作弊尝试会把后续运行结果标为 `cheat-modified`，但不会仅因此要求重启或禁止再次自动游玩；真正的自动化故障、协议不确定状态和运行时污染仍会保留彻底重启门禁。场景切换会关闭基地无敌、敌人 ID 显示、待捕获位置、已保存生成点和地图跳关等瞬态功能；Manager 连接或心跳丢失时，插件也会自动关闭作弊模式和这些瞬态功能。

为避免破坏波次账本，结束波次只允许用于正在进行的普通波次，模板锁定、没有活动波次或 Boss 波均会拒绝；刷怪只接受当前游戏配置中存在有效预制体的普通敌人，Boss 和特殊波单位不在允许列表。批量生成会在所选半径内保持间距，并逐个验证敌方阵营、战斗系统、受击组件和碰撞层；没有进入正常可攻击状态的对象会立即回收，不计入成功数量。清除所有敌人只清理已经生成的对象，波次计划中的后续敌人仍可能继续出现；需要结束波次时应使用单独的“结束当前波次”操作。

### 运行状态与彻底重启门禁

玩家模式安装后，插件可从当前用户的本机控制注册进入后台待命，手动启动游戏也无需一次性票据。两种模式都会保护 BepInEx 插件宿主跨场景存活；该保护不会开始自动游玩，也不会为玩家模式启用存档、平台写入或诊断产物重定向。隔离 QA 模式仍通过一次性票据激活；两种模式都不会修改游戏程序集。控制服务使用有界读写和四路监听，健康握手可绕过长命令；耗时命令会释放监听通道，Manager 使用同一请求 ID 获取最终缓存结果，同一操作不会重复执行。

自动化遇到不确定的部分写入、连续失败上限、停滞或隔离门禁故障时会进入 Faulted，并回传 `NeedsProcessRestart = true`。此时不能在同一游戏进程中开始新一轮：Manager 显示“必须彻底重启”，禁用“开始”并在命令发送层再次拒绝 `start`。单纯执行过作弊写操作只会标记结果，不再触发该门禁。开发截图可使用 `--demo-restart-required` 复现真正的重启门禁；该参数隐含 demo 模式，不连接真实游戏。

安全握手通过后的运行按钮矩阵如下；“安装、启动测试包、更新”等管理按钮不属于此矩阵：

| 状态 | 开始 | 暂停 | 继续 | 停止 |
|---|---:|---:|---:|---:|
| 握手未通过 | 禁用 | 禁用 | 禁用 | 禁用 |
| Standby / Completed，且无需重启 | 启用 | 禁用 | 禁用 | 禁用 |
| Running | 禁用 | 启用 | 禁用 | 启用 |
| Paused | 禁用 | 禁用 | 启用 | 启用 |
| Faulted 或 `NeedsProcessRestart = true` | 禁用 | 禁用 | 禁用 | 禁用 |

## 安全边界

- 玩家常驻模式使用玩家原存档和游戏原有平台行为，明确不安装 QA 存档、平台写入或诊断产物重定向补丁；任何意外启用都会使握手失败。该模式适合玩家主动使用，不提供测试隔离保证。
- 隔离 QA 模式通过游戏的 `SavePathUtility.GetCompanyAppDataPath` 契约把存档重定向到独立 profile。
- 隔离 QA 模式的平台写入门禁是强制条件：必须同时成功阻断 Steam 成就、IGP 成就、结算飞书自动上传和 `RestartAppIfNecessary` 四个已知入口，否则不能开始自动游玩。
- 隔离 QA 模式还必须把游戏诊断产物重定向到本次 artifact；重定向失败同样拒绝开始。
- 开局默认防线若明确报告 `prepared=false`、`statePolluted=false` 且不要求 reset，会视为初始化暂态并在干净状态下重试，不累计普通连续失败；返回结果任意嵌套层出现 `statePolluted=true`、`needsReset=true`，或证明动力站点放置已经提交但后续步骤失败，都会按不安全状态处理，停止并要求新进程。
- 前端写操作会等待游戏的全局模块与当前场景 Main 完成初始化，并要求就绪状态稳定一个轮询周期。进入对局后，合法路线与子关卡选择优先于默认防线，避免在随机模式路线图背后提前放置道具或绘线。
- “继续未完成对局”会操作当前模式正在使用的存档。成功执行 `continueGame` 后不会重新绘制开局默认防线，避免破坏存档中已有轨道和车辆。
- Harmony 补丁只存在于当前游戏进程内；退出游戏后失效。
- 隔离 QA 启动时设置的 `SteamAppId`/`SteamGameId` 只由本次游戏子进程继承，不写入 `steam_appid.txt`，也不修改系统级环境变量；玩家模式不注入这些变量。
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
src/Loopstructor.AutoPlayer.Manager/   .NET 8 Windows 管理器与共享自包含运行时
src/Loopstructor.AutoPlayer.Updater/   独立更新进程
tests/                                 自动化测试
scripts/                               bootstrap、构建、测试和打包脚本
docs/                                  架构、安全与发布说明
```

架构和数据流见 [docs/architecture.md](docs/architecture.md)。

## GitHub 与自动更新

push 和 pull request 会运行构建与测试；推送 `v*` tag 会生成唯一的 Windows x64 Release ZIP、对应 SHA-256 和 `autoplayer-update-manifest.json`，随后发布 GitHub Release。`Loopstructor.AutoPlayer-0.5.1-win-x64.zip` 同时用于手动下载和新版自动更新，内部只有固定的 `Loopstructor 2.AutoPlayer\` 顶层目录。更新器从同一个 Release 获取清单指定的 ZIP，并在替换前校验文件大小、SHA-256、固定目录结构和包内 `autoplayer-release.json`。

安装更新时会打开独立的更新窗口，按“准备、下载、校验、安装、重启”显示阶段进度。下载阶段显示已下载大小、总大小和实时速度；解压与事务替换阶段显示安装进度，完成后会显示最终结果。开始替换文件后窗口会锁定关闭操作，避免破坏更新或回滚。

自动更新只支持当前发布目录：Updater 固定为 `manager\Loopstructor.AutoPlayer.Updater.exe`，不再创建或接受旧 `updater\` 兼容目录。固定目录 `Loopstructor 2.AutoPlayer\` 无需随版本重命名。Manager 打开后，标题区会永久显示 `AutoPlayer 版本 v<当前版本>`，不依赖选择或加载游戏目录，更新检查状态也不会覆盖该版本文本；实际版本同时记录在 `autoplayer-release.json`。GitHub Actions artifact 下载时仍由平台套一层 ZIP，但打开后直接是扁平的程序文件和根部 Manager EXE，不包含 `Loopstructor 2.AutoPlayer\` 包装目录或第二层产品 ZIP。

默认发布与更新源为 [`yingyu4451/gui2`](https://github.com/yingyu4451/gui2)，Manager 界面不再显示仓库地址输入框，也无需手工填写。旧版本遗留的空白更新源会自动迁移为该默认值；开发测试 fork 时仍可用环境变量 `LOOPSTRUCTOR_AUTOPLAYER_GITHUB_OWNER` 和 `LOOPSTRUCTOR_AUTOPLAYER_GITHUB_REPOSITORY` 临时覆盖。

从 `v0.1.4` 起，公开仓库且未提供 token 时，更新器会先访问 GitHub 网页端的 `releases/latest`，核对它跳转到同一仓库的精确版本 tag，再从该 tag 的 Release 资产地址下载清单和 ZIP。此路径不调用匿名 GitHub REST API，因此不受匿名 API 每个出口 IP 每小时 60 次的配额影响。

提供 token（包括访问私有仓库）时，更新器才通过 GitHub REST API 查询 Release，并使用 API 返回的资产 URL。token 只发送给 `api.github.com`，跟随下载重定向到 GitHub Release CDN 时不会转发。不要把 token 写入源码、Manager 设置、发布包或日志。

从 `v0.1.7` 起，AutoPlayer 插件、Manager、Launcher 和 Updater 自己生成的运行消息与错误说明均使用中文；GitHub 的 401、403 和 429 会显示对应状态码及中文处理建议。BepInEx、Unity、游戏本体和系统组件直接输出的原始日志仍保持原文，以免丢失第三方诊断信息。

`v0.1.3` 在未提供 token 时仍使用匿名 REST API；如果它正显示 `403 (rate limit exceeded)`，可以等待 GitHub 配额恢复、仅在当前 Manager 进程环境中临时提供只读 token，或手动下载并安装 `v0.1.4` 一次。进入 `v0.1.4` 后，公开仓库的无 token 更新会改用上述网页 Release 路径。
