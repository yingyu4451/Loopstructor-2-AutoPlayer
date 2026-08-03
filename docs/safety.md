# 安全与测试隔离

## 基本原则

AutoPlayer 是进程内灰盒 QA 工具，不是完全黑盒输入测试。它的安全目标是：普通启动不自动操作、自动测试不接触正常存档、不向 Steam/IGP 写入测试成就、不触发结算飞书自动上传、未知构建不盲目运行，并为失败保留可审计证据。

它不会修改磁盘上的 `Assembly-CSharp.dll`，但安装 BepInEx 会在游戏根目录增加 `winhttp.dll`、`doorstop_config.ini` 和 `BepInEx` 目录。测试“完全未注入的原始发布包”仍应另设一小套外部启动和渲染冒烟测试。

Manager 在启动游戏子进程前完成测试包校验、创建一次性激活上下文，并以进程级环境变量传入 QA profile、artifact、pipe、token 和预期程序集哈希。BepInEx 会先创建共享的 Manager GameObject；AutoPlayer 只有在 `ActivationContext` 验证成功后，才对该对象调用 `DontDestroyOnLoad` 并设置 `HideAndDontSave`，使激活适配器隐藏且跨场景存活。普通启动会在这一步之前返回，不安装自动化补丁，也不改变共享 Manager 对象。这项保护只存在于本次已激活 QA 进程，不写入游戏配置或程序集。

当前 BepInEx 与该 Unity Mono 构建组合在完整游戏路径包含非 ASCII 字符时会触发程序集 `CodeBase` 转换错误；这不是未注入游戏本体的路径限制。Manager 必须在安装和启动前拒绝该路径，并要求把测试包移到只含 ASCII 字符的目录。路径可以包含英文字母、数字和空格。

## 失效即关闭门禁

自动游玩只有在以下条件同时成立时才允许进入 Running：

1. 存在合法的一次性 `ActivationContext`；
2. pipe 名称和本次随机 token 合法；
3. profile 与 artifact 是工具数据根目录下的绝对路径；
4. 当前游戏根目录与启动票据绑定值一致；
5. 实际 `Assembly-CSharp.dll` SHA-256 与管理器启动时记录值一致；
6. 产品身份与支持的构建指纹通过；
7. `GuiGameAutomation.Runtime` 必需方法全部存在；
8. QA 存档重定向已安装，并由运行中的 `SaveManager` 验证实际目录确实位于本次 profile；
9. 四个必需的平台写入/重启入口全部安装成功，`PlatformWritesBlocked = true`；
10. 游戏诊断产物重定向安装成功，`GameArtifactsRedirected = true`。

任一条件失败都应显示明确原因并保持 Standby/Incompatible/Faulted，不能降级到不隔离的自动运行。
即使一次性 QA 票据本身有效，也不能跳过存档、平台写入或产物重定向门禁；票据只授予本次握手机会，不代表隔离已经生效。

## 一次性激活与 IPC

- 普通启动没有激活环境或票据，插件不开放固定控制端点，也不自动开始游玩。
- pipe 名称和 token 每次启动重新生成；不要使用固定名称、固定口令或把 token 写入配置文件。
- 票据最长有效期为 10 分钟，绑定标准化后的游戏根目录，并在读取后立即删除，不能重放。
- 每个 IPC 请求必须携带本次 token；请求 ID 只用于关联响应，不能替代认证。
- Named Pipe 仅用于本机管理器与本次游戏进程。不要增加 TCP 监听或允许远程控制。
- 日志和异常消息不得输出 token、完整票据或私有仓库凭据。

## QA profile

测试 profile 必须位于：

```text
%LOCALAPPDATA%\LoopstructorAutoPlayer\profiles\<game-id>\<qa-profile-id>
```

插件在激活进程内重定向游戏的存档路径契约。操作前应确认状态中同时显示：

- `SaveIsolationApplied = true`；
- `SaveIsolationVerified = true`；
- `IsolatedSaveRoot` 指向预期 QA profile；
- 原始玩家存档目录没有新增或更新时间变化。

存档隔离是两阶段握手，而不是“补丁调用成功”即完成。插件先给 `SavePathUtility.GetCompanyAppDataPath` 及其内部实现安装 Harmony 前缀，再反射调用运行中的 `SaveManager.GetSaveFolderPath`，把实际解析路径与本次 profile 做规范化包含检查。Manager 的 `hello` 校验同时要求 `SaveIsolationApplied`、`SaveIsolationVerified` 和 profile 路径与启动票据一致；控制器在验证完成前不发出任何游戏命令，验证失败或超过等待窗口会进入 Faulted。

不要把已有玩家目录复制为 profile 根路径，也不要用符号链接把 QA profile 指回真实存档。需要测试旧档升级时，应复制一份脱敏存档到新的 QA profile，保留原始样本只读备份。

“继续游戏”只会继续本次隔离 QA profile 中已经存在的存档。该路径不会运行“新局默认防线”宏，也不会重建轨道、车列或站点，因此会保留存档中的既有布局。若需要可重复的新局布局，应使用新的空 QA profile，而不是在继续存档上强制重置。

隔离补丁目前覆盖游戏的 `SavePathUtility.GetCompanyAppDataPath` 契约。若新版游戏新增 PlayerPrefs、注册表、其他绝对路径或云存档写入，必须扩展并重新验证隔离门禁，不能假定它们自动受到保护。

## 作弊模式

作弊能力随可信 Manager 会话提供，但启用仍是独立的显式动作：

- 不需要在启动前选择特殊作弊会话。由 Manager 启动并通过握手的进程都携带作弊能力，实际功能仍需在独立作弊工具窗口中手动开启；
- 作弊与自动游玩共用当前选定的隔离 QA profile，绝不能选择真实玩家存档。写命令执行前会原子创建持久污染标记；该 profile 此后只能继续用于作弊测试，重启不会清除标记，需要干净基线时必须使用新的 QA 配置名称；
- 隔离门禁未通过、作弊协议不匹配或未手动启用时，写命令全部拒绝；
- 仅携带作弊能力或执行只读目录查询不阻止自动游玩；实际开启作弊模式时与自动游玩互斥；
- 在已启用的作弊模式中，任何会改变状态的操作都先持久标记 profile；标记无法确认时拒绝调用游戏 API。操作一经尝试，无论成功、失败、超时还是响应结果不确定，都设置 `CheatUsed` 和 `NeedsProcessRestart`，本次运行及该 profile 的后续运行都不能作为干净测试证据；
- 场景切换会关闭基地无敌、敌人 ID 显示和怪物位置捕获等瞬态功能。Manager 连接或心跳租约丢失时，插件会自动关闭作弊模式和全部瞬态功能，不能留下无人控制的持续修改；
- 作弊操作写入本次 artifact 下的审计记录；不得把 token、票据或私有仓库凭据写入审计文件。

高风险动作额外失效即关闭：结束波次只接受正在进行的普通非 Boss 波次；模板锁定、没有活动波次或 Boss 波一律拒绝。生成怪物只接受当前游戏配置中具有有效预制体的普通敌人白名单，Boss 和特殊波单位不能生成，并继续执行数量、坐标和对象有效性限制。清除敌人只清除当前已经生成的对象，不改变波次后续生成计划，避免把“清屏”误当成正常完成波次。

作弊工具支持获取带多种自选附魔的指定战车、获取消耗品和两类弹射点、基地无敌、结束当前波次、清除当前敌人、修改指定车辆属性、修改指定敌人属性、显示敌人运行时 ID、获得遗物及在指定位置生成允许的怪物。目录名称和图标来自游戏运行时配置；怪物位置可在捕获状态下用左 Alt 加鼠标左键固定。捕获逻辑挂在游戏输入采样完成点，并在后续游戏交互前消费成功点击；等待两分钟或关闭作弊工具窗口会自动取消，补丁不可用时功能保持关闭。这些能力只用于可丢弃 QA 环境；启用基地无敌或直接修改属性后的玩法结果不得与普通回归结果混合统计。

## Steam 与 IGP 写入

激活会话会阻断当前已知成就写入入口：

```text
ActFramework_ByHZR.Achievements.Unit.SteamAchievementController.UnlockAchievement
ActFramework_ByHZR.MainLoop.Version.PlatformAchievementBridge.ReportIGPAchievement
MetroTD.UISystem.SettlementDataTotalManager.TryAutoSendResultOnce
Steamworks.SteamAPI.RestartAppIfNecessary
```

`PlatformWritesBlocked` 只有在上述四个入口全部找到并成功安装补丁时才为 true；少一个都视为门禁失败。有效 QA 票据不会放宽这个要求。该保证只覆盖已经识别并成功补丁的入口，不代表 Steam Cloud、统计、排行榜、购买、遥测或第三方 SDK 的所有网络写入均被阻断。

即使已知写入全部被拦截，Steam 在线状态、游戏时长、Overlay、客户端启动记录以及第三方 SDK 的连接行为仍可能留下平台侧痕迹。要求“零平台痕迹”时，应使用不含平台 SDK 的测试包；做不到时至少使用离线环境和专用测试账号，不能依赖本补丁保护正式账号。

游戏更新后若类型或方法签名改变，应把构建标记为不兼容，更新补丁和测试后再恢复自动运行。

激活会话还会把 `GameSettlementData`、`ObjectUseData`、本地化启动日志和已注册的游戏调试日志重定向到本次 artifact。`GameArtifactsRedirected` 未通过时自动运行保持不兼容，避免清空或污染游戏目录中人工测试留下的诊断文件。

Manager 仅在启动所选 QA 包的子进程时设置 `SteamAppId=3841840` 与 `SteamGameId=3841840`，避免 Steam 把它替换成库中另一份安装；它不会创建 `steam_appid.txt` 或修改永久环境。激活插件同时让 `RestartAppIfNecessary` 返回 false，作为第二道进程内边界。Steam API 初始化与许可证校验仍照常执行，工具不会伪造许可或绕过 `SteamAPI_Init`。当前本机/账号对 AppID `3841840` 的许可校验失败会阻断 Steam 支持的测试运行；应改用有许可的 QA 账号/机器、离线方案或无平台测试包，而不是更换 AppID 或关闭门禁。即使启动失败，指纹和根目录绑定也会阻止工具跟随操作另一份构建。

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

- 确认选择的是测试包，不是 Unity 工程或玩家安装目录的错误副本；
- 记录 `Assembly-CSharp.dll` SHA-256，并确认构建在兼容清单中；
- 创建新的 QA profile 或明确选择可丢弃的旧 QA profile；
- 确认 Steam/IGP 使用 QA 账号或离线环境；
- 确认证据磁盘空间充足。
- 需要作弊调试时，确认当前选择的是可丢弃的隔离 QA profile；不需要单独选择启动会话。

握手后：

- 检查产品身份、指纹和 runtime contract 全部通过；
- 检查存档隔离已应用且已验证；
- 检查平台写入阻断状态；
- 普通自动游玩会话再发送 `start`。
- 开启作弊模式前先停止自动游玩；应打开作弊工具并手动启用，确认界面显示运行时门禁已通过。

运行后：

- 停止自动玩家并正常退出游戏；
- 检查真实存档和平台账号没有测试写入；
- 归档失败证据并记录游戏构建哈希；
- 只要尝试过作弊写操作，就彻底关闭游戏进程后再开始任何干净测试；
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
