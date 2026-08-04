# 安全与测试隔离

## 基本原则

AutoPlayer 是进程内灰盒自动游玩工具，不是完全黑盒输入测试。它有两种明确边界：玩家常驻模式允许手动启动后随时控制，直接使用玩家原存档与游戏原有平台行为；隔离 QA 模式用于可审计回归，不接触正常存档，并阻断当前已知的 Steam/IGP 成就、结算飞书上传和 Steam 重启入口。两种模式都不会自动开始操作，未知构建也不会盲目运行。

它不会修改磁盘上的 `Assembly-CSharp.dll`，但安装 BepInEx 会在游戏根目录增加 `winhttp.dll`、`doorstop_config.ini` 和 `BepInEx` 目录。测试“完全未注入的原始发布包”仍应另设一小套外部启动和渲染冒烟测试。

Manager 安装后会为所选游戏创建当前 Windows 用户专用的玩家模式本机注册，绑定游戏根目录、协议、pipe、随机 token 和预期程序集哈希。因此手动启动游戏也能进入经过本机认证的待命状态，但不会自动开始游玩。隔离 QA 模式另行创建一次性激活上下文，并以进程环境或单次票据传入 QA profile、artifact、pipe、token 和预期程序集哈希。任何激活失败都不得开放控制；这些机制都不写入游戏程序集。

当前 BepInEx 与该 Unity Mono 构建组合在完整游戏路径包含非 ASCII 字符时会触发程序集 `CodeBase` 转换错误；这不是未注入游戏本体的路径限制。Manager 必须在安装和启动前拒绝该路径，并要求把测试包移到只含 ASCII 字符的目录。路径可以包含英文字母、数字和空格。

## 失效即关闭门禁

自动游玩只有在以下共同条件成立时才允许进入 Running：激活来自合法的玩家本机注册或一次性 QA 上下文；pipe 与 token 合法；工具状态目录未越界；当前游戏根目录、实际进程路径和 `Assembly-CSharp.dll` SHA-256 均匹配；产品身份、构建指纹和 `GuiGameAutomation.Runtime` 必需方法全部通过。

模式门禁必须二选一且不能混用：

- `ResidentPlayer`：`SaveIsolationApplied`、`SaveIsolationVerified`、`PlatformWritesBlocked` 和 `GameArtifactsRedirected` 必须全部为 false，确保玩家原存档和平台语义没有被 QA 补丁悄悄替换。
- `IsolatedQa`：上述四项必须全部为 true，确保 QA 存档、平台写入和诊断产物隔离都已经安装并验证。

任一条件失败都应显示明确原因并保持 Standby/Incompatible/Faulted，不能在两种模式之间静默降级。

## 本机激活与 IPC

- 玩家模式使用 `%LOCALAPPDATA%\LoopstructorAutoPlayer\control\installed-<root-id>.json` 中的稳定 pipe 和高熵 token，使同一用户手动启动的受信游戏能够被 Manager 发现；这不代表会自动开始游玩。
- 玩家注册必须同时绑定规范化游戏根目录、协议和程序集指纹；卸载插件时应删除对应注册。不要把该文件提交、共享或复制到其他用户。
- 隔离 QA 模式的 pipe 与 token 每次启动重新生成；票据最长有效期为 10 分钟，绑定游戏根目录，并在读取后立即删除，不能重放。
- 每个 IPC 请求必须携带本次 token；请求 ID 只用于关联响应，不能替代认证。
- Named Pipe 仅用于本机管理器与本次游戏进程。不要增加 TCP 监听或允许远程控制。
- 日志和异常消息不得输出 token、完整票据或私有仓库凭据。

## QA profile

隔离 QA profile 必须位于：

```text
%LOCALAPPDATA%\LoopstructorAutoPlayer\profiles\<game-id>\<qa-profile-id>
```

只有 `IsolatedQa` 会重定向游戏的存档路径契约。操作前应确认状态中同时显示：

- `SaveIsolationApplied = true`；
- `SaveIsolationVerified = true`；
- `IsolatedSaveRoot` 指向预期 QA profile；
- 原始玩家存档目录没有新增或更新时间变化。

存档隔离是两阶段握手，而不是“补丁调用成功”即完成。插件先给 `SavePathUtility.GetCompanyAppDataPath` 及其内部实现安装 Harmony 前缀，再反射调用运行中的 `SaveManager.GetSaveFolderPath`，把实际解析路径与本次 profile 做规范化包含检查。Manager 的 `hello` 校验同时要求 `SaveIsolationApplied`、`SaveIsolationVerified` 和 profile 路径与启动票据一致；验证失败时拒绝控制。玩家模式不执行这两个阶段，并会拒绝任何意外出现的隔离标志。

不要把已有玩家目录复制为 profile 根路径，也不要用符号链接把 QA profile 指回真实存档。需要测试旧档升级时，应复制一份脱敏存档到新的 QA profile，保留原始样本只读备份。

“继续游戏”只会继续本次隔离 QA profile 中已经存在的存档。该路径不会运行“新局默认防线”宏，也不会重建轨道、车列或站点，因此会保留存档中的既有布局。若需要可重复的新局布局，应使用新的空 QA profile，而不是在继续存档上强制重置。

隔离补丁目前覆盖游戏的 `SavePathUtility.GetCompanyAppDataPath` 契约。若新版游戏新增 PlayerPrefs、注册表、其他绝对路径或云存档写入，必须扩展并重新验证隔离门禁，不能假定它们自动受到保护。

## 作弊模式

作弊能力随可信 Manager 会话提供，但启用仍是独立的显式动作：

- 不需要在启动前选择特殊作弊会话。通过握手的玩家模式或隔离 QA 进程都携带作弊能力，实际功能仍需在独立作弊工具窗口中手动开启；
- 玩家模式直接修改当前玩家存档，工具不会自动备份或回滚；隔离 QA 模式只修改当前测试 profile。两种模式的写命令执行前都会在工具状态目录原子创建持久作弊标记；标记失败时拒绝写入；
- 玩家模式只有在 QA 存档、平台和产物补丁全部未启用时才能开启作弊；隔离 QA 模式只有在三类门禁全部验证后才能开启。作弊协议不匹配或未手动启用时，写命令全部拒绝；
- 仅携带作弊能力或执行只读目录查询不阻止自动游玩；实际开启作弊模式时与自动游玩互斥；
- 在已启用的作弊模式中，任何写尝试都设置 `CheatUsed`，后续自动游玩结果标为 `cheat-modified`。关闭作弊模式后可在同一进程继续自动游玩；只有自动化故障或无法判断是否发生部分写入时才设置 `NeedsProcessRestart`；
- 场景切换会关闭基地无敌、敌人 ID 显示、怪物位置捕获、已保存生成点和地图跳关等瞬态功能。Manager 连接或心跳租约丢失时，插件会自动关闭作弊模式和全部瞬态功能，不能留下无人控制的持续修改；
- 作弊操作写入本次 artifact 下的审计记录；不得把 token、票据或私有仓库凭据写入审计文件。

高风险动作额外失效即关闭：结束波次只接受正在进行的普通非 Boss 波次；模板锁定、没有活动波次或 Boss 波一律拒绝。地图跳关允许当前地图界面已加载阶段中的已通过、当前和未来节点，但活动波次、运行节点、待选子关卡、陈旧阶段请求、跨阶段和失效目标一律拒绝；执行失败时恢复原阶段和路径，恢复失败则自动关闭该功能。生成怪物只接受当前游戏配置中具有有效预制体的普通敌人白名单，Boss 和特殊波单位不能生成，并继续执行数量、坐标和对象有效性限制；批量生成在指定半径内保持间距，每个对象还必须通过敌方阵营、敌方碰撞层、战斗系统和可受击状态验证，否则立即回收。清除敌人只清除当前已经生成的对象，不改变波次后续生成计划，避免把“清屏”误当成正常完成波次。

作弊工具支持获取或删除带多种自选附魔的战车、编辑已有战车附魔（`0` 级移除目标项）、获取消耗品、获取或按实际背包行删除两类弹射点、单删或清空场上弹射点、基地无敌、结束波次、清除敌人、修改车辆或敌人、显示敌人 ID，以及获取或删除遗物。所有选择器同时显示中文名、枚举名和图标，并可按中文名、枚举名或稳定 ID 搜索；对象战车还显示已有附魔图标。怪物生成点可在捕获状态下用左 Alt 加鼠标左键反复添加，游戏内显示编号十字与坐标，工具中可单删或清空。生成默认复用当前波次 `CurrentAILevel` 及游戏正式难度倍率链，显式自定义等级才覆盖；每点按半径分散，生成后还要通过阵营、碰撞、战斗和可受击校验。玩家模式使用这些能力前应备份存档；任何带作弊标记的结果都不得与干净回归结果混合统计。

## Steam 与 IGP 写入

只有隔离 QA 会话会阻断当前已知成就写入入口：

```text
ActFramework_ByHZR.Achievements.Unit.SteamAchievementController.UnlockAchievement
ActFramework_ByHZR.MainLoop.Version.PlatformAchievementBridge.ReportIGPAchievement
MetroTD.UISystem.SettlementDataTotalManager.TryAutoSendResultOnce
Steamworks.SteamAPI.RestartAppIfNecessary
```

`PlatformWritesBlocked` 只有在上述四个入口全部找到并成功安装补丁时才为 true；少一个都视为隔离 QA 门禁失败。玩家模式不安装这些补丁，`PlatformWritesBlocked` 必须为 false。该保证只覆盖已经识别并成功补丁的入口，不代表 Steam Cloud、统计、排行榜、购买、遥测或第三方 SDK 的所有网络写入均被阻断。

即使已知写入全部被拦截，Steam 在线状态、游戏时长、Overlay、客户端启动记录以及第三方 SDK 的连接行为仍可能留下平台侧痕迹。要求“零平台痕迹”时，应使用不含平台 SDK 的测试包；做不到时至少使用离线环境和专用测试账号，不能依赖本补丁保护正式账号。

游戏更新后若类型或方法签名改变，应把构建标记为不兼容，更新补丁和测试后再恢复自动运行。

激活会话还会把 `GameSettlementData`、`ObjectUseData`、本地化启动日志和已注册的游戏调试日志重定向到本次 artifact。`GameArtifactsRedirected` 未通过时自动运行保持不兼容，避免清空或污染游戏目录中人工测试留下的诊断文件。

Manager 仅在隔离 QA 启动的子进程中设置 `SteamAppId=3841840` 与 `SteamGameId=3841840`，并让 `RestartAppIfNecessary` 返回 false；它不会创建 `steam_appid.txt` 或修改永久环境。玩家模式不注入 AppID，也不拦截平台行为。Steam API 初始化与许可证校验仍照常执行，工具不会伪造许可或绕过 `SteamAPI_Init`。

## 对游戏文件的影响

- 不修改、重签名或替换 `Assembly-CSharp.dll`。
- 读取 `Assembly-CSharp.dll`、Build GUID、Unity 版本等信息用于兼容性判定。
- Harmony 只修改当前进程中的方法执行路径；进程退出后失效。
- 安装器只能管理自己清单中的文件。若游戏已有其他 BepInEx 插件，不得用“删除整个 BepInEx 目录”的方式卸载。
- 更新或卸载必须在游戏和管理器相关进程退出后执行。

## 运行中保护

- 自动玩家通过 `GuiGameAutomation.Runtime` 调用游戏正常流程，不移动真实鼠标、不发送系统键盘事件，因此可在后台测试。
- 后台运行不代表可以忽略图形设备或窗口生命周期；最小化、失焦和设备重置仍应纳入回归用例。
- 连续失败和停滞检测会停止动作。不得通过无限提高阈值来掩盖新版不兼容。
- Faulted 后先保存状态、日志和截图，再结束进程；不要自动删除失败现场。
- `pause` 只暂停自动决策，游戏本身是否继续计时由游戏状态决定；需要冻结游戏时应使用游戏明确支持的暂停流程。

## 游戏动作与污染处理

- 新局默认防线只在通过普通/随机模式提交后执行。无回路、无车列、无已放置玩家车辆等“干净但尚未就绪”的失败可以继续轮询，不计为连续命令失败；继续已有隔离存档不会执行该宏。
- 默认防线会把子命令结果嵌套在返回结构中。检查器会递归查找任意深度的 `statePolluted = true` 或 `needsReset = true`；即使外层声称干净，只要嵌套结果证明动力站点放置已提交而后续步骤失败，也会立即按 Unsafe 处理、停止动作并要求全新游戏进程。不得在同一进程中再次运行宏来覆盖污染现场。
- 前端写操作仅在游戏全局模块和当前场景 Main 均完成初始化、且就绪状态跨一个轮询周期保持稳定后发出。进入 `NewGameScene` 后若仍可选择路线或子关卡，必须先完成路线流程，再准备默认防线。
- 路线决策只接受 `canPlayerSelect = true` 的候选；该值由节点当前的 ready/可用状态共同决定。候选为空或全部不可选时必须等待，不能让 `FirstOrDefault` 把“无结果”解释成 `readyIndex = 0`。
- `selectMapNode` 的命令成功还必须带回已提交的 `chooseNode` 或 `pendingSubLevelNode`。插件会再次检查该后置条件；只有调用返回成功、但没有产生节点状态变化时，仍按失败处理，不能每个 Tick 重复点击同一节点。

## 证据与隐私

artifact 目录可能包含：游戏截图、存档路径、构建版本、程序集哈希、当前路线、奖励和错误信息。上传 CI 或 GitHub Issue 前应检查是否包含账号名、绝对路径或未公开内容。

建议的保留策略：

- 成功运行只保留摘要和关键指标；
- 失败运行保留完整状态、日志和截图；
- token 与启动票据永不归档；
- QA profile 按测试批次清理，清理前确认路径位于工具的 `profiles` 根目录。

## 每次运行检查表

运行前：

- 确认选择的是打包游戏，不是 Unity 工程目录；
- 记录 `Assembly-CSharp.dll` SHA-256，并确认构建在兼容清单中；
- 玩家模式先备份重要存档；隔离 QA 模式创建新的 profile 或明确选择可丢弃的旧 profile；
- 隔离 QA 模式确认 Steam/IGP 使用 QA 账号或离线环境；
- 确认证据磁盘空间充足。
- 需要作弊时不必单独选择启动会话；玩家模式确认接受修改当前存档，隔离 QA 模式确认 profile 可丢弃。

握手后：

- 检查产品身份、指纹和 runtime contract 全部通过；
- 玩家模式检查四个 QA 隔离标志均为 false；隔离 QA 模式检查它们均为 true；
- 确认真实 PID、进程路径、程序集指纹和运行时契约均通过后再发送 `start`；
- 开启作弊模式前先停止自动游玩；应打开作弊工具并手动启用，确认界面显示运行时门禁已通过。

运行后：

- 停止自动玩家并正常退出游戏；
- 隔离 QA 模式检查真实存档和平台账号没有测试写入；玩家模式检查实际存档变化符合预期；
- 归档失败证据并记录游戏构建哈希；
- 尝试过作弊写操作后，后续结果会保持 `cheat-modified`；需要干净测试证据时使用新的隔离 QA profile 与进程；
- 需要卸载时只删除工具拥有的文件。

## 已知测试真实性限制

BepInEx、Harmony 和反射调用都会改变进程内环境，因此本工具可以验证大量玩法逻辑与长时间稳定性，但不能单独证明以下内容：

- 原始正式包在完全无注入环境下启动正常；
- 真实鼠标、键盘、手柄、焦点切换和输入映射正常；
- 启动器、覆盖层、反作弊或平台客户端集成正常；
- 所有动画与 UI 点击区域可由玩家实际完成。

当前构建还有两个已知环境/游戏观察项，不应被自动玩家隐藏：

- 普通模式与随机模式均已完成 2 波启动、1 波完成的跨波验证。随机转盘离场后，游戏仍会从 `RandomMode_TurnTableManager.StopDecorateAnimation` 的延迟回调记录非致命 `Animator.Play` 空引用；该异常未被 Harmony 屏蔽，需由游戏侧继续修复。
- 本机/账号对 Steam AppID `3841840` 的 `SteamAPI_Init` 许可校验失败；涉及 Steam 初始化的结果只能在有许可环境或无平台测试包中复验。

这些项目应由独立黑盒冒烟测试补充。
