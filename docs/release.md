# 构建、发布与更新

## 固定依赖

当前发布链固定以下版本：

| 依赖 | 版本 |
|---|---|
| .NET SDK | `8.0.423`，由 `global.json` 与 bootstrap 同时固定 |
| BepInEx runtime | `5.4.23.5` Windows x64 |
| BepInEx 编译包 | `BepInEx.Core 5.4.21` |
| Unity 编译引用 | `UnityEngine.Modules 2022.3.62` |
| 插件目标框架 | `netstandard2.1`（对应当前 Unity 2022 项目的 `apiCompatibilityLevel: 6`） |
| Desktop | Electron `44.x`、Node `24` 构建链、Vue 3、TypeScript、Vite、Pinia、Tailwind CSS、离线 Iconify |
| Host/Updater RID | `win-x64`；Host 与无窗口 .NET Updater 均随包发布，无需系统 .NET |

BepInEx runtime 下载地址和 SHA-256 集中在 `Directory.Build.props`：

```text
https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip
SHA-256: 82f9878551030f54657792c0740d9d51a09500eeae1fba21106b0c441e6732c4
```

打包脚本会在解压前验证该哈希。不要改成“自动下载 BepInEx 最新版”，否则上游发布会绕过本项目的兼容性测试。

## 本地构建和测试

在仓库根目录执行：

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\bootstrap.ps1
.\scripts\build.ps1 -Configuration Release
.\scripts\test.ps1 -Configuration Release -NoRestore -NoBuild
```

`bootstrap.ps1` 下载固定 SDK zip、验证 SHA-512 后安装到 `.dotnet`。`build.ps1` 使用仓库的 `NuGet.config`，仅启用 nuget.org 和 BepInEx 官方 feed，并通过冻结的 pnpm lockfile 构建 Electron/Vue 前端。`test.ps1` 把 TRX 写入 `artifacts\TestResults`，并运行 TypeScript、ESLint 与 Vitest 验证。

## 0.6.61 更新运行时暂存与主按钮可读性

`0.6.61` 修复 Electron 更新入口在 Windows 上复制当前运行中的 Manager 目录时可能于大文件或共享运行时文件中途失败、最终只留下不完整 `electron-*` 临时目录并显示“无法准备独立的更新运行环境”的问题。暂存器现在将 Electron 启动 EXE 独立复制，确保新进程与当前运行中的 Manager 不共用同一映像文件；其余同卷运行时优先创建硬链接，避免重新读取大文件。更新器通过目录移动完成事务替换，不会就地改写硬链接内容。临时目录位于不同卷、硬链接权限不足或达到链接数量限制时，才逐文件回退到排他复制。失败会立即清理本次不完整目录，不再积累半成品。

Skyspine 皮肤中的所有亮黄色实心主按钮不再使用深色文字，统一改为深铜渐变铭牌、暖金外轮廓和浅金文字；选择目录、安装/修复、保存、应用设置、生成与开始等主操作保持一致。hover 与 focus-visible 只提升亮度和绿色焦点轮廓，文字对比保持稳定。Electron 主进程测试同时验证启动 EXE 为独立文件、其余运行时使用硬链接，并使用真实 `0.6.59` 安装目录完成 265 文件暂存验证。本版本以 `v0.6.60` 为相邻增量基线；协议、目录格式和更新清单 schema 均未改变。

## 0.6.60 机械 UI 状态与层级修复

`0.6.60` 修复 Skyspine 皮肤在 hover、active、selected、focus-visible 和 disabled 之间发生 cascade 竞争的问题。基础布局规则进入低优先级 `app-base` layer；Skyspine 模块通过组件状态 token 保留金属渐变、齿轮背景和结构阴影，禁用状态只降低明度与饱和度，不再用全局 `!important` 抹掉材质。选中导航、目录卡、选项卡和皮肤项都有独立 hover 组合，键盘焦点沿控件自身异形轮廓增强，不再叠加被 `clip-path` 裁断的矩形焦点框。

异形面板、按钮、导航和目录行改为外轮廓与内材质两层绘制，清理 `clip-path + border + outline` 产生的断角和粗细不一。工作台原生框图改成只覆盖边缘的 chrome overlay，并统一顶部铭牌、左右管线和底部齿轮安全区；每个页面只显示一个可见标题，作弊控制条位于铭牌下方。紧凑高度下缩短导航节奏并保留滚动安全区，底部节点不再被裁切。Playwright 新增 hover、focus、selected、disabled、chrome z-index/mask 和标题间距断言与截图。本版本以 `v0.6.59` 为相邻增量基线；协议、目录格式和更新清单 schema 均未改变。

## 0.6.59 游戏原生机械终端 UI

`0.6.59` 纠正 `0.6.58` 只改主题令牌、但组件剪影和材质变化不足的问题。标题栏、左侧导航、主内容区、作弊控制条、11 个路由及各页面原有功能分组保持原位；可见组件全部重做为游戏原生机械终端：标题栏贯穿齿轴和驱动齿轮、导航齿轮座和推出铭牌、主工作台机械管线外框、铆钉切角面板、金属压入按钮、荧光数据槽、齿轮窗口控制和机械更新终端。

默认 `skyspine` 皮肤直接复用 Unity 工程中的原生地图背景、机械框、车站装饰、齿轮和游戏字体。皮肤注册表、`data-skin` 作用域、Host 持久化字段和 GSAP 动效接口继续保留，后续皮肤可替换整套资产、轮廓、布局令牌和动效；旧 `mechanical`/`signal` 设置会迁移到 `skyspine`。插件协议、Desktop Host 协议、目录格式和更新清单 schema 未改变。本版本以 `v0.6.58` 作为相邻增量更新基线。

CI 与 Release workflow 同步升级到 Node 24 action：`actions/checkout@v7`、`actions/setup-node@v7`、`actions/cache@v5`、`pnpm/action-setup@v6`、`actions/upload-artifact@v7` 和 `actions/download-artifact@v8`。重新下载发布产物后的结构验证也不再要求已经移除的 WPF 运行时 DLL，因此 `v0.6.59` 及后续版本可以继续完成构建、打包、产物回读验证和 GitHub Release 发布。

## 0.6.58 可换皮机械仪表盘与 GSAP 动效

`0.6.58` 保留现有标题栏、分组导航、作弊控制条、11 个路由、IPC 和数据绑定，重构可见层为更接近游戏参考图的机械仪表盘：深蓝钢底板、铜轨、黄铜铭牌、信号灯、纹理面板和更清晰的操作层级。首版内置“齿轨工坊”和“信号夜航”两套皮肤；皮肤契约可同时切换颜色、材质、边框、圆角、间距、导航宽度和组件形状，不是单纯换色。

皮肤 ID 由 Host 设置模型校验并持久化，未知值回退到“齿轨工坊”。前端新增 GSAP `3.15.0`，用于页面进入、忙碌反馈和更新进度；系统启用“减少动态效果”时自动跳过这些动画。插件协议、Desktop Host 协议、目录格式和更新清单 schema 未改变。本版本以 `v0.6.57` 作为相邻增量更新基线。

## 0.6.57 Electron-only 桌面界面与无窗口更新事务

`0.6.57` 删除了不再进入产品构建的旧 WPF Manager 窗口、页面、主题、缩放服务、演示数据和对应布局测试。当前唯一可见的 Manager 与更新界面均由 Electron 44 + Vue 3 提供；`src/Loopstructor.AutoPlayer.Manager` 目录只保留 Host 复用的安装、会话、存档与更新服务模型。

独立 Updater 保留下载、清单校验、增量重建、事务替换、回滚和重启能力，但改为纯 `net8.0` 无窗口进程。`--json` 与 `--json-stream` 输出协议保持不变，Electron `UpdaterPage.vue` 继续消费 `progress/result` 事件；非 JSON 调用只输出控制台进度，不再启动 WPF 窗口。协议版本、目录格式和更新清单 schema 未改变。本版本以 `v0.6.56` 作为相邻增量更新基线。

## 0.6.56 更新窗口自适应与安装目录解锁

`0.6.56` 将 Manager 的最小窗口尺寸约束留在原生 `BrowserWindow`，不再把 `980×680` 强制施加到更新 renderer。更新卡片会在 `680×520` 的最小更新窗口和默认 `760×600` 窗口内自适应缩放，长错误消息自动换行，只有内容真实溢出时才在卡片内部滚动。

Electron 更新窗口启动后会先把当前 `manager` 运行时复制到 `%TEMP%\LoopstructorAutoPlayerUpdater\electron-*`，再由临时副本托管 Vue 更新界面和 .NET 更新事务。正式安装目录因此不再被 Electron 自身占用，可以完成原子替换；更新成功并重启新版 Manager 后会清理本次临时运行时。插件协议、作弊协议、目录格式和更新清单 schema 保持不变。本版本以 `v0.6.55` 作为相邻增量更新基线。

## 0.6.55 独立存档页与手动读档

`0.6.55` 将存档保险库从界面设置中拆为侧栏独立页面，列出当前游戏范围内全部受管快照、章节关卡、保存时间、文件数量和大小。选择“读档”后会先显示机械风确认窗口；确认后 Host 验证备份 ID 和当前玩家存档目录，请求 Skyspine 正常退出，在同一存档父目录完成暂存、内容校验、原存档回滚点和原子切换，失败时恢复读档前存档，成功后自动重新启动游戏。新增 `backups.list` 与 `backups.restore` 为 Desktop Host 内部向后兼容白名单 RPC；游戏插件协议、作弊协议和更新清单 schema 保持不变。本版本以 `v0.6.54` 作为相邻增量更新基线。

## 0.6.54 Electron 更新模式与流式进度

`0.6.54` 复用现有 Manager 的 Electron 运行时增加 `--updater` 模式。Host 启动更新时会打开同一个 Manager 可执行文件的 Vue 更新窗口，窗口不启动游戏 Host，而是托管现有 .NET Updater 的流式进度；更新器继续在隐藏临时副本中完成清单校验、完整或增量下载、解压、事务替换、回滚和更新后重启。Electron 页面只消费进度事件，不接触安装目录写入，因此包体只增加更新界面资源。

旧版 `check/apply --json` 调用保持兼容；`--json-stream` 是内部桥接选项，输出 `progress/result` 事件供 Electron 使用。若 Electron 入口不可用，Host 仍回退启动随包无窗口 .NET Updater，发布清单路径和校验格式不变。本版本以 `v0.6.53` 作为相邻增量更新基线。

## 0.6.53 存档保险库、Buff 详情与桌面界面修复

`0.6.53` 新增由 .NET Host 执行的正式玩家存档自动备份。插件只低频读取 `SaveManager.GetSaveFolderPath` 与章节关卡，不在 Unity 主线程复制文件；Host 检测到进度变化后等待写盘稳定，在临时目录复制，并比较复制前后文件清单指纹。只有指纹一致时才原子完成名为 `第01章-第003关-20260831-123456` 的快照。用户可在 Electron 设置页启用/关闭并设置最多保留 1–100 个步骤，保留清理只处理工具专属目录中名称严格匹配的最旧快照；隔离 QA 存档不会重复备份。

怪物 Buff 覆盖层改为锚定在模型下沿，避免遮挡头顶伤害数字。同类运行时 Buff 会聚合为层数徽标，图标下方显示持续时间以及当前对象可读取的减速率、移速倍率或效果值，悬停详情继续显示中文名、枚举名、层数和具体数值。本版本以 `v0.6.52` 作为相邻增量更新基线；插件协议、作弊协议、目录格式和更新清单 schema 均未改变。

战车获取恢复为上一版的初始形态和升级形态两个选择，不显示内部过渡等级；系列卡片直接渲染当前游戏目录返回的战车图标。界面设置在编辑期间不再被 Host 轮询覆盖，缩放后页面轨道和当前路由保持不变；标题栏的可用版本铭牌可直接进入安装流程。Electron 模态框和更新页使用同一套煤黑、深铜、黄铜、信号绿与禁用态色令牌，避免更新流程出现另一套窗口风格。

本地正式完整包为 221,984,564 字节（约 212 MiB），SHA-256 为 `d88c2706e37ce85edc1228757702e547e27c1fd3d0d935fa500f5225a49da544`；GitHub Release workflow 重新构建后的公开完整资产为 221,984,374 字节，SHA-256 为 `eb7a113384ee1f46027094dda2dfb50fdf46c72a8b274c32b4e291c554844598`。`v0.6.52 → v0.6.53` 本地增量包为 110,163,303 字节，公开 Release 资产为 110,163,113 字节，SHA-256 为 `60bfe5dfbee7a18dc1b0eed664e26f5b23437dde15371ddec5b74ef81ff2795e`，只包含 17 个变化文件。完整包 370 个文件及增量重建结果均已逐文件验证；安装和更新时以公开清单中的云端 SHA 为准。

## 0.6.52 导航状态、自动游玩入口与默认作弊

`0.6.52` 修复 Electron renderer 把当前页面存放在 Host 快照中导致的导航回跳：用户选择的页面现在由 renderer 单独持有，旧轮询快照和设置保存失败都不会把界面强制切回“游戏与插件”。自动游玩页面重新提供当前游戏动态模式与角色读取、速度、剧情、决策优先、开始/暂停/继续/停止和运行轨迹，同时始终显示“尚未完成”风险提示。

Host 新增向后兼容的自动游玩白名单 RPC，并继续使用现有插件协议和安全门禁构造运行参数。可信会话建立后，Host 自动开启作弊功能并在租约中断后重新连接时恢复；统一窗口删除手动“开启作弊”按钮。本版本以 `v0.6.51` 作为相邻增量更新基线；游戏插件协议、作弊协议、目录格式和更新清单 schema 均未改变。

## 0.6.51 Electron + Vue 统一工具窗口

`0.6.51` 将旧 WPF Manager 与独立作弊窗口迁移为 Electron 44 + Vue 3 统一窗口，使用分组齿轨侧栏切换游戏与插件、作弊目录、诊断和设置页面。可见图标全部来自随包离线安装的 Iconify MDI 集合，不依赖 Lucide、CDN 或在线页面；renderer 开启 sandbox、contextIsolation 和严格 CSP，只能通过 preload 的类型化白名单访问本机能力。

原 Manager 非 UI 服务由无窗口 `.NET 8 Host` 承接，通过内部 `desktopHostProtocolVersion: 1` 的逐行 JSON RPC 为 Electron 提供游戏验证、插件管理、可信会话、作弊命令、设置、日志和更新交接。自动游玩后端暂时保留但新界面不允许启动，只保留停止升级前遗留会话的入口。Updater 使用独立无窗口 .NET 进程。本版本以 `v0.6.50` 作为相邻增量更新基线；游戏插件协议、作弊协议、目录格式和更新清单 schema 均未改变。

## 0.6.50 主环优先与均衡外环

`0.6.50` 修复多轨道维护把第一环排除在插点候选之外的问题。游戏 `RailManager` 的稳定内部 ID 从 `0` 开始；旧版把 `railInternalId=0` 当作缺失身份，导致后建外环在吞吐排序中独占额外中继站。候选、必接地图站点、可移动特殊站点和战中完整重连现在都先按最早仍存在的轨道内部 ID 排序，再比较覆盖、回转周期和 `N/T`。

独立外环只会选择组成玩家合法闭环所需的一个始发站与两个中继站，不再一次吞入全部未分配站点。外环在计划、真实 `queryRail` 对账和持续拓扑门禁三个阶段都必须通过均衡防御环校验；除了无交叉、单连通和严格包围基地，还限制最大/最小角差及半径比。现场 `(-2,-2) / (-4,-3) / (5,4)` 这类最大角缺口接近 `178°` 的细长三角会被拒绝并进入站点移动或重连维护。本版本以 `v0.6.49` 作为相邻增量更新基线。

## 0.6.49 弹射点维护时序与轨神原生动画

`0.6.49` 修复第二波奖励后的弹射点虽已正确识别并放到场上，却在奖励结算尚未退出时提前进入防线维护的问题。防线状态机现在只在完整 `queryWave` 快照确认奖励、事件、道具预览和地图过渡均已退出后运行；奖励写入账本先完成只读对账，未接入站点的闭环门禁随后优先执行，完成前不会打开地图、选择节点或开波。不同维护阶段会刷新可验证进展，防止合法的分帧布局规划被通用超时看门狗误判。

旧版 `WaveFunctionUI` 的选项会在 3.2 秒未缩放延迟后生成，但这不代表触手的 Spine `Appear` 动画已经结束。轻量面板契约现在读取 `SpineAnimationController.GetCurrentAnimationName(0)`、`AnimationIsComplete(0, "Appear")` 和配置动画时长；只在原生动画真实完成后才开始 1 秒绿色选择预览。反射状态不可读时才使用游戏自身动画时长回退。本版本以 `v0.6.48` 作为相邻增量更新基线。

## 0.6.48 开波前弹射点库存与最近始发站

`0.6.48` 修复把“场上已有两个普通点”误当成“背包弹射点已经部署完毕”的问题。开局准备会逐个放置 `FreePoint` 与运行时发现的可移动特殊中继站，每次写入后重新查询库存；当某种库存仍存在但游戏当前确实没有合法格时，保留道具并记录该结论，不会盲点禁区，也不会阻塞其他种类。开局最终闭环必须接入本次部署的全部站点。

动力始发站候选仍直接读取当前游戏 `MapPosManager.EnergyCatapultRingPosition`，并通过真实 `GridChooseInteraction` 条件逐格复核可放/不可放。排序删除了额外向外预留一圈的人工半径，在最佳全向覆盖层中优先离基地最近的真实合法格，再比较最小间距余量与回路长度。本版本以 `v0.6.47` 作为相邻增量更新基线。

## 0.6.47 更新前安全关闭游戏

`0.6.47` 在用户确认安装更新后按当前已验证的 Skyspine 可执行文件路径检查游戏进程。若游戏仍在运行，Manager 会显示与现有机械铭牌、黄铜边框和状态色一致的确认窗，列出对应 PID 并询问是否“关闭游戏并更新”。确认后仅请求游戏正常退出并最多等待 20 秒；确认退出后才启动 Updater。用户取消、游戏拒绝正常关闭或等待超时时，本次安装停止且 Manager 保持可用，不会调用强制结束进程。本版本以 `v0.6.46` 作为相邻增量更新基线。

## 0.6.46 动态禁放范围与开局站点预留

`0.6.46` 重新核对游戏 `1.390` 的 `MapPosManager`、`CheckCatapultIsValid`、`CatapultCreator` 和玩家网格交互实现。站点的单项最小间距仍是当前配置中的 `2.1` 格，并没有在本局中自行变大；但每成功部署一个站点，游戏都会从普通与动力候选池中删除其周围的合法格，所以所有已部署站点形成的总禁放区域会逐次扩大。

旧版把动力始发站放在离基地最近的合法格，再逐项读取已经缩小的候选池，可能得到一枚近点和数枚远点，最后才被均衡圆环门禁拒绝。本版本在第一次确认前读取游戏当前普通/动力最小间距，在最内合法半径之外预留一整条间距带；普通站点首点保持约 120° 分散并优先匹配始发站半径，后续普通/特殊站点在写入前排除会突破最终半径比或不能形成均衡闭环的候选。动力站点只从动力候选池、中继站只从普通候选池取格，单格提交前仍由游戏原生条件复核。本版本以 `v0.6.45` 作为相邻增量更新基线。

## 0.6.45 开局防御圆环与特殊弹射点

`0.6.45` 根据游戏 `1.390` 的现场坐标复现了错误布局：两枚普通中继站位于 `(-4,-4)`、`(-8,-6)`，动力始发站位于 `(16,14)`。这个三点多边形可能通过游戏的 `isLoop`，但半径跨度过大、两个站点角度几乎重叠，画面表现为一条细长折线，不能覆盖四面来敌。

本版本把真实线段拓扑、包围基地、最小/最大角差和半径比一起作为开局硬门禁；预览计划与 `queryRail` 提交后结果都必须是分布均衡的防御圆环，响应缺少真实端点时也不允许仅凭 `isLoop` 放行。同时在开波前读取 `queryDisposable.effectFacts`，动态发现 `canAlwaysMove=true` 的特殊中继站，逐个放置后标记为最终闭环的必选站点；特殊始发站仍交给独立新回路扩建事务。现场背包里的“闪电路径弹射点”因此不会再被普通三站开局流程跳过。本版本以 `v0.6.44` 作为相邻增量更新基线。

## 0.6.44 背包普通弹射点识别修复

`0.6.44` 修复开局准备把场上站点查询误当成背包库存查询的问题。`queryCatapults` 仍只返回已经放到战场的站点；当场上普通站点不足两枚时，自动游玩改为读取 `queryDisposable` 中的 `FreePoint` 数量、按钮可用状态、交互类型和稳定道具身份，逐个完成候选格探测、确认和场上验证。每次成功放置后重新读取背包身份，再继续下一枚普通点；普通点就绪后同样重新读取 `FreePoint_Attribute` 身份再放置动力点。

普通点与动力点共享同一套增量候选格、交互守卫、预览所有权和 pending 写入只读对账流程；目标格出现对应枚举的站点即可证明延迟确认成功，不会重复发送写命令。新增测试覆盖“背包有 2 枚普通点、场上为 0”以及“已有两枚普通点后切换动力点身份”的流程。本版本以 `v0.6.43` 作为相邻增量更新基线。

## 0.6.43 构建与隔离 QA 记录

2026-08-29 使用游戏开发包 `1.390`（Unity `2022.3.62f3c1`，Build GUID `b11b48d5c56b4efdb37026b58dbac8fa`）执行验证。Release 构建零警告，`scripts/test.ps1 -Configuration Release` 的 694 项测试全部通过，发布资产校验确认 ZIP 中 298 个文件与 staging 逐字节一致。

| 工件 | SHA-256 |
|---|---|
| `Assembly-CSharp.dll` | `5fe335080178a72b8874bf8afe689d5360ff14f0624286abab4b7853c4c4327c` |
| `Loopstructor.AutoPlayer.Plugin.dll` | `845f418e79fc50b61c83fa5d049862f0307916f7deebfac6cb6781c6fa337fed` |
| `Loopstructor.AutoPlayer.Core.dll` | `1c1d86b5069899568d1e58945d23e2c31103a490b7e2f7490aac6faece3d6c86` |
| `Loopstructor.AutoPlayer.Manager.dll` | `a830032b681936eea99de919c7e5dce4aacf52107673fc6c9f958341e1d81fd3` |
| `Loopstructor.AutoPlayer-0.6.43-win-x64.zip` | `a113a5aeaa9658ef4b340cf68f326fe8a92a65d2ed0ddc5882d414592896e437` |

最终发布载荷的隔离工件为 `%LOCALAPPDATA%\LoopstructorAutoPlayer\artifacts\70b8472d5fcb0cde\20260829-021003-b297f174`。插件 `0.6.43` 握手成功，运行时契约可用，程序集指纹被接受，`SaveIsolationVerified`、`PlatformWritesBlocked` 和 `GameArtifactsRedirected` 均为 true，`RunIntegrity=clean` 且 `NeedsProcessRestart=false`。无作弊运行在 7 分钟窗口内完成 5 波并推进到第 1 章第 8 层；窗口结束时是正常 `TimedOut`，没有伪报到达第 2 章。

现场从同一容量服务重复读取动态容量、运行战车、FIFO 等待数、占用数和剩余名额，所有投放都按战车与唯一能量点实例提交，并在写后只读验证同一实例已运行。三次拓扑证据依次确认 1、2、3 条轨道全部为包围基地、无交叉的单一简单闭环；额外闭环只在已有合法轨道满载且背包仍有战车时创建，每条仅含一个能量点。两次装修厂流程都完成未升级战车锁定、三个稳定附魔候选、附魔选择、升级形态和原附魔保留复查及结算确认。该存档的投放在复查时均已转为运行态，现场没有出现非零 FIFO 瞬时样本；FIFO 顺序、`AlreadyQueued` 幂等、容量收缩回包、不同独立速度聚合与未知写入只读对账由 694 项测试中的容量服务契约和行为测试覆盖，不冒充现场观察。

`run-autoplay-qa.ps1` 支持用 `-SeedProfileRoot` 从 `%LOCALAPPDATA%\LoopstructorAutoPlayer\profiles` 下的既有隔离 profile 复制种子，并配合 `-ContinueExistingProfile` 走游戏原生继续入口。脚本拒绝目录越界、源目标相同和包含重解析点的种子；种子始终复制到新的随机隔离 profile，不直接修改原存档。

## 当前 1.385 真实包验证基线

2026-07-29 的本机验证使用 Windows x64、Unity `2022.3.62f3c1`、Skyspine `1.385` 和 BepInEx runtime `5.4.23.5`。每种模式都必须保留各自的端到端证据，不能由一种模式的结果推断另一种模式或 Steam 集成也已通过。

| 场景 | 结果 | 已观察证据 |
|---|---|---|
| 普通模式跨波 | `PLAY_OK` | `WavesStarted=2`、`WavesCompleted=1`；完成第 1 波、奖励物收集、奖励选择、波后选路并启动第 2 波 |
| 随机模式跨波 | `PLAY_OK` | 完成随机车辆/羁绊选择、路线事件与节点选择；`WavesStarted=2`、`WavesCompleted=1`，奖励与波后选路均已观察 |
| 存档隔离 | 通过 | QA profile 独立生成存档；真实玩家目录原有 4 个文件的哈希和时间未改变 |
| 游戏程序集完整性 | 通过 | 测试前后 `Assembly-CSharp.dll` SHA-256 均为 `962b9f69774d4ea20458877363bc33bc7f85cfa207baa1d775b0ea5677140f29` |
| 安全门禁 | 通过 | `SaveIsolationVerified`、`PlatformWritesBlocked`、`GameArtifactsRedirected` 均为 true |

随机模式首次验证在场景模块尚未完成 `Init()` 时过早调用入口，曾于 `Temp` 场景触发 `MetroTDAffixCreatorHandler.Clear()` 空引用。插件现在只读检查全局模块与当前场景 Main 的加载状态，并要求连续一个稳定轮询周期后才执行前端写操作；随后随机模式已完成跨波复验。当前 Player.log 仍会记录随机转盘离场后的 `RandomMode_TurnTableManager.StopDecorateAnimation` 非致命 `Animator.Play` 空引用；工具不以 Harmony 吞掉该异常，发布说明应保留这个游戏侧已知问题。

隔离 QA 启动子进程时使用生产 AppID `3841840`。本机缺少该 AppID 许可，Player.log 记录 `[Steamworks.NET] SteamAPI_Init() failed`；这没有阻止上述隔离玩法测试，但也不能作为 Steam 已完全离线或账号不会留痕的证据。玩家模式不注入 AppID，也不阻断平台行为。已知 QA 补丁只覆盖 Steam 成就、IGP 成就、结算飞书自动上传及 `RestartAppIfNecessary` 四个入口；正式验收应使用专用 QA 账号、离线环境或无平台测试包。

运行时恢复语义也属于发布兼容性：玩家模式必须能连接手动启动的受信游戏，且待命时不重定向玩家存档或平台行为；隔离 QA 仍保持跨场景激活保护。普通战败、超时、只读失败和有界重试耗尽可以 Faulted，但不得设置 `NeedsProcessRestart`；只有不确定部分写入、明确污染标志或隔离门禁失效才要求彻底重启。此时 Manager 必须禁用 Start 并拒绝向旧进程发送新的 `start`。作弊写尝试只设置 `CheatUsed` 和 `cheat-modified`；作弊模式本身不阻止自动游玩，但开始前必须关闭基地无敌和地图节点自由跳转等持续效果。

默认防线的干净初始化暂态会重试且不累计连续失败；当前写命令有效结果中的 `statePolluted=true`、`needsReset=true`，以及无法确认回滚的部分写入，必须被识别为不安全并要求新进程。历史快照中的旧标志不得触发污染判定；动力站点步骤曾提交但最终状态已验证为无轨道、无运行或等待战车，且所有战车实例都能在背包对账时，应作为干净检查点重试。路线/子关卡选择必须先于开局防线；“继续 QA 存档”成功后不得再次执行开局默认防线宏，以免改写既有轨道。以上行为应由单元测试和真实包日志共同覆盖。

## 0.5.2 独立运行时宿主验收

现场日志证明 `0.5.1` 的插件虽然对 BepInEx 管理对象调用了 `DontDestroyOnLoad`，该对象仍会在游戏第一次正式场景装载时被销毁，`OnDestroy` 随即关闭控制管道。`0.5.2` 将控制服务、控制器、Harmony 补丁和 Unity 帧回调迁移到隐藏的独立根对象；BepInEx 组件只负责验证激活并启动静态会话，其销毁不再释放运行时。独立宿主若意外消失，静态会话会通过 `sceneLoaded`、`activeSceneChanged` 和 `onBeforeRender` 在主线程限频重建；只有应用退出信号才执行逐项、失败互不影响的最终清理。

发布前必须用程序集契约测试锁定 bootstrap、独立宿主、重建回调、退出事件和最终清理的所有权，且 Pipe 服务至少有一个监听器成功绑定后才能记录激活成功。同一进程出现部分初始化失败时必须失败关闭并要求重启，不能复用残留的静态补丁状态。真实包还应从启动场景进入主菜单并跨至少一次地图或战斗场景，持续握手不少于 60 秒；游戏退出前 BepInEx 日志不得出现“AutoPlayer 运行时正在退出”。

## 0.5.1 玩家常驻与作弊模式验收

`0.5.1` 引入了 BepInEx 管理对象跨场景保护、有界读写和四路并发监听；现场复验随后确认，仅保护 BepInEx 管理对象仍不足以跨过游戏的首次场景装载，因此生命周期修复由 `0.5.2` 的独立运行时宿主取代。Manager 使用相同请求 ID 取得耗时命令的最终缓存结果，并在首次断线时立即禁用控制、重新握手。玩家模式使用 PID 专属端点，绑定同时核对可执行路径、进程启动时间与随机进程实例标识；多个未绑定的同目录游戏进程不会被任意选择。游戏目录中的旧插件若与当前发布载荷不一致，Manager 会要求重新安装而不会把它误报为可用。

玩家常驻验收必须覆盖：安装后手动启动游戏也能连接；Manager 最小化后保持后台且不因作弊窗口操作抢到前台；玩家模式四个 QA 隔离标志均为 false；PID、路径、程序集指纹、协议和 token 任一不符都会拒绝。作弊工具逐项覆盖中文名、枚举名、家族排序和图标搜索；附魔左加右减及全量卡片换行；消耗品/弹射点互斥分类；获取/删除战车、遗物、背包及场上弹射点；一键逐帧补齐或删除全部遗物的进度、幂等跳过和部分失败；场上点击删除的精确命中、Esc/场景/断连复位；战斗操作；以及多点怪物生成。

仅携带或实际启用作弊能力都必须允许 `start`，但基地无敌或地图跳关仍开启时必须拒绝。自动游玩运行或暂停期间只开放目录/实体查询以及敌人 ID、Buff 覆盖层，其余作弊写命令由 Manager 和插件双重拒绝；覆盖层不设置 `CheatUsed`。获准的写操作尝试要持久标记 `cheat-modified`，但不单独要求重启。场景切换必须关闭基地无敌、敌人 ID、位置捕获、生成点列表与地图跳关；Manager 断连或心跳超时必须关闭作弊模式及瞬态功能。背包弹射点删除必须从 `ownedCatapultPoints` 选择真实 `catapultPointId`，不得仅按枚举删任意同类 Buff 行。怪物生成默认等级必须与 `WaveProgressController.CurrentAILevel` 一致，多点按每点数量分散，且每个对象通过阵营、碰撞、战斗和受击验证。

## 本地打包

发布 ZIP 使用 7-Zip 的标准 Deflate 极限参数，在保持 Windows 和内置更新器兼容的前提下减小下载体积。本地打包机需要安装 7-Zip；未加入 `PATH` 时可向 `package.ps1` 传入 `-SevenZipPath`。

完整构建、发布并打包：

```powershell
.\scripts\package.ps1 -Version 0.6.53
```

已经完成同版本 Release 构建时：

```powershell
.\scripts\package.ps1 -Version 0.6.53 -SkipBuild
```

版本必须是 SemVer。脚本生成：

```text
artifacts/release/
Loopstructor.AutoPlayer-0.6.53-win-x64.zip
Loopstructor.AutoPlayer-0.6.53-win-x64.zip.sha256
Loopstructor.AutoPlayer-0.6.52-to-0.6.53-win-x64.delta.zip        可选
Loopstructor.AutoPlayer-0.6.52-to-0.6.53-win-x64.delta.zip.sha256 可选
  autoplayer-update-manifest.json
```

完整 Release ZIP `Loopstructor.AutoPlayer-0.6.53-win-x64.zip` 始终用于手动下载、首次安装、跨版本升级和增量不可用时的回退。必须先完整解压，不能直接在资源管理器的 ZIP 预览中运行。压缩包内只有固定的 `Loopstructor 2.AutoPlayer\` 顶层目录，目录名不包含版本号；进入该目录后才是程序根目录：

```text
Loopstructor 2.AutoPlayer/
  Loopstructor.AutoPlayer.Manager.exe   根目录单文件启动器
  manager/
    Loopstructor.AutoPlayer.Manager.exe Electron 桌面入口
    resources/app.asar                  Vue renderer 与 Electron 主进程
    Loopstructor.AutoPlayer.Host.exe     无窗口 .NET Host
    Loopstructor.AutoPlayer.Host.dll
    Loopstructor.AutoPlayer.Updater.exe  无窗口 .NET 更新事务入口
  payload/
    bepinex/
    plugin/
  autoplayer-release.json
  version.json
  checksums.sha256
```

固定目录无需随版本升级而重命名。完整解压后运行根部 EXE 无需安装 Node.js 或系统 .NET；根启动器、Electron Desktop、.NET Host 与无窗口 .NET Updater 均包含在发布包中。发布包不再创建或接受旧 `updater\` 兼容目录。更新应用前，Updater 会把自身和所需运行时一起复制到临时目录，因此仍能安全替换整个程序目录。标题栏永久显示当前产品版本；实际版本同时记录在程序根部的 `autoplayer-release.json`。

`payload\bepinex` 必须是经过固定哈希验证的 BepInEx `5.4.23.5` Windows x64 运行时；不得在打包时自动漂移到最新版。`payload\plugin` 只包含 AutoPlayer Plugin、Core 和必要的第三方运行依赖。发布包不得包含 `Assembly-CSharp.dll`、其他游戏 DLL、Unity 测试引用、QA profile、Player.log、状态/截图等测试工件、token 或启动票据；`Assembly-CSharp.dll` 也不得被复制或修改。

## 更新清单

GitHub Release 根资产 `autoplayer-update-manifest.json` 的协议版本为 2：

```json
{
  "schemaVersion": 2,
  "version": "0.6.53",
  "runtimeIdentifier": "win-x64",
  "assetName": "Loopstructor.AutoPlayer-0.6.53-win-x64.zip",
  "sha256": "eb7a113384ee1f46027094dda2dfb50fdf46c72a8b274c32b4e291c554844598",
  "size": 221984374,
  "deltaAssets": [
    {
      "fromVersion": "0.6.52",
      "assetName": "Loopstructor.AutoPlayer-0.6.52-to-0.6.53-win-x64.delta.zip",
      "sha256": "60bfe5dfbee7a18dc1b0eed664e26f5b23437dde15371ddec5b74ef81ff2795e",
      "size": 110163113
    }
  ]
}
```

`deltaAssets` 是 schema 2 的可选扩展。协议版本有意保持为 2，使旧 Updater 可以忽略未知字段并继续下载完整包。增量资产只为精确的相邻基准版本生成；没有上一正式 Release、旧包校验失败或增量包不小于完整包时，发布仍然成功，但不包含增量资产。

公开仓库且未提供 token 时，更新器不调用匿名 GitHub REST API。它先访问 `https://github.com/<owner>/<repository>/releases/latest`，只接受跳转到同一仓库的精确版本 tag；随后通过该 tag 的 `releases/download/<tag>/...` Release 资产地址下载清单和 ZIP。这样不会消耗匿名 REST API 每个出口 IP 每小时 60 次的配额，也避免在清单下载后继续使用可变化的 `latest` 地址。

提供 token（包括访问私有仓库）时，更新器才调用 GitHub REST API，并使用同一个 API Release 返回的资产 URL。凭据只发送给 `api.github.com`；资产下载跳转到 GitHub Release CDN 后不得转发 `Authorization`。两种路径都不能信任清单中附带的任意下载 URL，并且必须确认精确 Release tag、清单 `version` 和版本化 `assetName` 一致。下载后依次验证：

1. `schemaVersion` 支持；
2. `version` 是比当前版本新的 SemVer；
3. `runtimeIdentifier` 为 `win-x64`；
4. 当前安装 marker 的版本与增量 `fromVersion` 精确一致，否则使用完整包；
5. 下载字节数与所选资产的 `size` 相同；
6. zip SHA-256 与所选资产的 `sha256` 相同；
7. 完整 ZIP 只有名称和大小写精确为 `Loopstructor 2.AutoPlayer/` 的顶层目录；
8. 增量 ZIP 只有固定的 `Loopstructor 2.AutoPlayer.delta/` 顶层目录、目标版 `checksums.sha256` 和发生变化的 `files/`；
9. 增量更新在空 staging 中复制当前安装的未变文件、写入增量文件并自然排除已删除文件；
10. staging 根存在目标版 `autoplayer-release.json`，且全部文件通过目标版 `checksums.sha256` 和完整发布包结构校验。

验证完成后退出管理器，再由独立 Updater 使用事务安装器替换工具目录。更新开始提交前会再次核对基准版本；旧目录只作为隐藏临时回滚点存在，新版完整校验成功后立即删除，不保留可供手动降级的副本。任何验证或替换失败都会恢复当前可运行版本，不能半更新后继续启动游戏。更新继续使用固定的 `Loopstructor 2.AutoPlayer\` 目录，无需随版本重命名；实际版本以 Manager GUI 和 `autoplayer-release.json` 为准。

当前 Updater 只处理 schema 2 和当前固定包装目录。Updater 入口必须是 `manager/Loopstructor.AutoPlayer.Updater.exe`，包含旧 `updater/` 兼容目录的发布包会被拒绝；旧目录版本需要手动安装当前发布包。`v0.5.3` 是首个支持增量更新的客户端，因此 `v0.5.2 → v0.5.3` 仍需完整下载一次；安装 `v0.5.3` 后，后续相邻版本才会选择增量包。跳过版本时使用完整包，不串联多个历史增量。

`v0.1.3` 的无 token 更新检查仍调用匿名 REST API。如果它正报告 `403 (rate limit exceeded)`，可等待配额恢复、仅在当前 Manager 进程环境中临时提供只读 token，或手动下载并安装 `Loopstructor.AutoPlayer-0.1.4-win-x64.zip` 一次。安装 `v0.1.4` 后，公开仓库的无 token 更新改用网页端 `releases/latest` 和精确 tag 的 Release 资产地址。

## GitHub Actions

`.github/workflows/ci.yml` 在 branch push、pull request 和手工触发时执行：

1. 恢复或 bootstrap 固定 SDK；
2. Release 构建；
3. 测试；
4. 上传 TRX。

`.github/workflows/release.yml` 在 `v*` tag push 时执行：

1. 从 tag 提取 SemVer；
2. 构建和测试；
3. 生成带固定 `Loopstructor 2.AutoPlayer/` 顶层目录的完整 Release ZIP、SHA-256 与 schema 2 更新清单；
4. 下载并严格验证上一正式版本的已发布完整包，生成可选的文件级增量 ZIP 和 SHA-256；
5. 对完整包和增量重建结果执行逐文件、结构、marker 与安全验证；
6. 上传未压缩目录作为 workflow artifact，并重新下载验证根部 EXE、marker、checksums 且不存在内嵌产品 ZIP；
7. 先创建草稿 Release，上传完整包与增量资产，最后上传清单并公开；失败草稿重跑时从空资产集合重建，已经公开的同 tag Release 不允许自动覆盖。

手工触发 Release workflow 只生成 artifact，不自动创建没有对应 tag 的正式 Release。GitHub 下载 artifact 时固定使用外层 ZIP；与 Release ZIP 不同，解开 Actions artifact 后应直接得到扁平的程序文件和根部 Manager EXE，不应出现 `Loopstructor 2.AutoPlayer/` 包装目录或第二层产品 ZIP。

## 仓库与首次发布

Git 仓库和 `origin` 已配置为 [`yingyu4451/Loopstructor-2-AutoPlayer`](https://github.com/yingyu4451/Loopstructor-2-AutoPlayer)，默认分支为 `main`。Manager 与 Updater 的默认更新源使用相同坐标，Manager 界面不提供仓库地址输入框；旧版 `settings.json` 中的空白坐标及 `yingyu4451/gui2` 旧坐标会在加载和保存时迁移为该默认值，因此用户无需手工填写。环境变量可在不改包的情况下临时覆盖到测试 fork。

仓库可见性决定客户端认证方式：

- 公开仓库在无 token 时使用网页端 `releases/latest` 和精确 tag 的 Release 资产地址，不调用匿名 REST API，也不占用每个出口 IP 每小时 60 次的匿名 API 配额；
- 提供 token 或访问私有仓库时使用 GitHub REST API 的资产 URL；token 只能发送给 `api.github.com`，不得转发给 Release CDN；
- 私有仓库必须设计 token 的安全存储、最小权限和撤销流程，不能把个人访问令牌编译进程序。

发布前先确认本地提交已经推送到正确远端：

```powershell
git remote -v
git status --short --branch
git fetch origin
```

在 GitHub 仓库 Settings 中允许 GitHub Actions 对 contents 写入，确认 CI 通过后发布：

```powershell
git tag v0.6.53
git push origin v0.6.53
```

仅创建本地 tag 不会发布；必须把 tag 推送到已配置的 GitHub remote。

如果仓库保持私有，每台测试机都必须通过进程环境或受控的秘密管理器提供只读 `LOOPSTRUCTOR_AUTOPLAYER_GITHUB_TOKEN`。不要使用 `setx` 永久保存 token，不要把 token 写入 Manager 设置、仓库、发布包或日志；下载重定向到 Release CDN 时也不得携带该 token。公开仓库不需要 token。

## 发布检查表

- Core、Plugin、Host、Updater 和 Tests 全部在 solution 中且 Release 构建成功；Electron/Vue 的冻结依赖恢复、类型检查、ESLint、Vitest 和 Playwright 均通过；
- 测试覆盖玩家本机注册、一次性 QA 票据、token 拒绝、路径越界拒绝、程序集哈希不符和更新哈希失败；
- 使用新的空 QA profile 分别完成普通模式和随机模式跨波验证；记录 2 波启动、1 波完成、奖励和波后选路，或记录当前版本的新等价证据；
- 验证所有前端写操作只在全局模块和当前场景 Main 稳定就绪后发出；检查随机模式日志并记录转盘离场后的非致命动画异常是否仍存在；
- 确认真实存档目录文件哈希和时间未变化，且 QA profile 独立产生存档；
- 确认四个强制平台写入补丁全部应用；使用 QA 账号或离线环境，不得把“无已知成就写入”等同于账号零痕迹；
- 验证干净的默认防线初始化失败会重试、嵌套污染或已提交动力站点后的失败会要求新进程、路线先于防线、继续 QA 存档不会重建默认防线；
- 验证 Faulted/`NeedsProcessRestart` 后 Manager 禁用 Start 且拒绝向旧游戏进程发送 `start`；
- 验证玩家模式可连接手动启动游戏且不启用任何 QA 重定向；统一窗口单实例、侧栏、响应式页面和作弊控制条正确；自动游玩页不可启动且可停止遗留会话；中文/枚举/Iconify 图标搜索、战车/附魔家族排序、附魔无限层数与卡片全量换行、消耗品/弹射点互斥目录、对象图标、已有附魔图标、批量删除和多点生成等作弊能力符合预期；
- 验证地图跳关仍隐藏当前进度层及历史层，只开放进度之后的节点，并拒绝活动波次、运行节点、待选子关卡、陈旧阶段请求、跨阶段及失效目标；验证失败补偿恢复和恢复失败自动关闭；
- 验证结束波次拒绝无活动波次、模板锁定和 Boss 波；指定位置刷怪拒绝 Boss、特殊波单位和无有效预制体的 ID，批量位置在所选半径内保持间距，且每个成功对象都处于敌方阵营并具备正常碰撞、战斗和可受击状态；
- 验证普通事件剧情开关只点击 `EventUI_Normal` 的真实 Skip 按钮，轨神事件不受影响；两种决策优先级可持久化并改变奖励与路线排序；右侧目标型道具只使用最新 MCP 合法候选，扩轨资源不会被战斗逻辑消耗；
- 验证每条轨道只有一个能量点，动态容量以运行数加 FIFO 等待数计入占用；容量未满时按独立战车实例投放，发射点繁忙时安全排队，重复请求幂等，容量收缩的溢出战车回包，写入结果未知时只读对账且绝不重放；
- 验证所有合法轨道满载且背包仍有战车时才创建只含一个能量点的新闭环；扩轨收益按逐车基础输出、独立速度、轨道长度和站点数计算，断轨前后按运行实例集合及等待顺序对账；
- 验证装修厂优先直升真实且未升级的战车，并完整走过选择战车、确认、稳定三选一附魔和结算阶段；同名个人附魔优先升级，既有个人附魔全部保留且附魔数量不设上限；
- 验证作弊快捷投放每个战车系列只显示“初始形态 / 升级形态”，内部过渡形态和车列专属附魔不出现在新增或设置目录，旧存档已有车列专属附魔仍可查看和移除；旧决策配置值 `0` 加载为“优先拿战车”；
- 在受支持构建上验证运行时契约检查允许启动和执行；在程序集指纹或必需运行时契约未知的构建上验证插件拒绝写入并返回明确的不兼容原因；
- 将完整 Release ZIP 完整解压，确认它只有固定的 `Loopstructor 2.AutoPlayer\` 顶层目录；进入后验证根启动器无需系统 Node.js/.NET 即可启动 Electron、Host 与 Updater，`resources/app.asar` 存在、不存在旧 `updater\` 目录，并验证 marker 和逐文件 checksums；不得在 ZIP 预览中运行；
- 验证 schema 2 更新清单的完整包资产名、大小和 SHA-256 正确；存在 `deltaAssets` 时，还要从对应已发布基线重建并逐文件比对目标包；
- 分别验证公开仓库无 token 时不调用匿名 REST API，以及带 token 时只向 `api.github.com` 发送凭据且不向 Release CDN 转发；验证精确 tag、清单版本和 ZIP 资产名不一致时拒绝更新；
- 重新下载 Actions artifact，确认打开后直接是扁平的程序文件和根部 Manager EXE，不含 `Loopstructor 2.AutoPlayer\` 包装目录或第二层产品 ZIP；
- 发布后续版本时，用采用当前目录结构的前一版本执行一次完整自更新与失败回滚测试；旧 `updater\` 目录结构不在兼容范围内；
- 检查发布包固定使用 BepInEx `5.4.23.5`，且不含 `Assembly-CSharp.dll`、其他游戏 DLL、Unity 测试引用、token、票据、QA 存档、日志、状态或测试截图；
- 发布说明记录游戏构建指纹、程序集哈希、BepInEx `5.4.23.5`、两种模式的验证状态、随机转盘非致命异常、Steam AppID `3841840` 的本机许可限制及账号残余风险；
- GitHub 发布与更新坐标保持为 `yingyu4451/Loopstructor-2-AutoPlayer`；若仓库私有，确认测试机通过安全环境提供只读 token，且发布包、日志和 Git 历史均不含 token。

## 升级 BepInEx

升级 BepInEx 时必须手工完成以下步骤：

1. 只从 BepInEx 官方 GitHub Release 选择 Windows x64 的 BepInEx 5 稳定包；
2. 记录精确版本、资产 URL 和 SHA-256；
3. 同时更新 `Directory.Build.props` 中三个 runtime 属性；
4. 检查编译包 API 兼容性；BepInEx runtime `5.4.23.5` 与 NuGet `BepInEx.Core 5.4.21` 的版本差异是已知且有意的；
5. 重跑构建、单元测试、安装、普通启动待机、激活启动和卸载测试；
6. 通过新的项目版本发布，不能静默替换旧 tag 的 BepInEx 载荷。
