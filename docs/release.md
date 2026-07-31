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
| Manager/Updater RID | `win-x64`；Manager 自包含目录携带唯一一套运行时，Updater 复用该运行时 |

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

`bootstrap.ps1` 下载固定 SDK zip、验证 SHA-512 后安装到 `.dotnet`。`build.ps1` 使用仓库的 `NuGet.config`，仅启用 nuget.org 和 BepInEx 官方 feed。`test.ps1` 把 TRX 写入 `artifacts\TestResults`。

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

Manager 启动子进程时使用生产 AppID `3841840`。本机缺少该 AppID 许可，Player.log 记录 `[Steamworks.NET] SteamAPI_Init() failed`；这没有阻止上述两种模式的隔离玩法测试，但也不能作为 Steam 已完全离线或账号不会留痕的证据。已知补丁只强制阻断 Steam 成就、IGP 成就、结算飞书自动上传及 `RestartAppIfNecessary` 四个入口；在线状态、游戏时长、Overlay、云同步、统计或新版新增遥测仍可能被平台记录。正式验收应使用专用 QA 账号、离线环境或无平台测试包，发布说明不得承诺账号“零痕迹”。

运行时恢复语义也属于发布兼容性：激活进程内的 BepInEx Manager GameObject 由 `DontDestroyOnLoad` 和 `HideAndDontSave` 保护，跨场景存活但不永久修改 BepInEx 配置。任何污染/故障会设置 `NeedsProcessRestart=true`；Manager 必须显示“必须彻底重启”、禁用 Start 并拒绝向旧进程发送新的 `start`。正常 Running 只启用 Pause/Stop，Paused 只启用 Resume/Stop，未握手或需要重启时运行命令全部禁用。

默认防线的干净初始化暂态会重试且不累计连续失败；任何深层包装中的 `statePolluted=true`、`needsReset=true`，以及已提交动力站点后发生的后续失败，都必须被识别为不安全并要求新进程。路线/子关卡选择必须先于开局防线；“继续 QA 存档”成功后不得再次执行开局默认防线宏，以免改写既有轨道。以上行为应由单元测试和真实包日志共同覆盖。

## 本地打包

完整构建、发布并打包：

```powershell
.\scripts\package.ps1 -Version 0.1.9
```

已经完成同版本 Release 构建时：

```powershell
.\scripts\package.ps1 -Version 0.1.9 -SkipBuild
```

版本必须是 SemVer。脚本生成：

```text
artifacts/release/
  Loopstructor.AutoPlayer-0.1.9-win-x64.zip
  Loopstructor.AutoPlayer-0.1.9-win-x64.zip.sha256
  autoplayer-update-manifest.json
```

唯一的 Release ZIP `Loopstructor.AutoPlayer-0.1.9-win-x64.zip` 同时用于手动下载和新版自动更新。必须先完整解压，不能直接在资源管理器的 ZIP 预览中运行。压缩包内只有固定的 `Loopstructor 2.AutoPlayer\` 顶层目录，目录名不包含版本号；进入该目录后才是程序根目录：

```text
Loopstructor 2.AutoPlayer/
  Loopstructor.AutoPlayer.Manager.exe   根目录单文件启动器
  manager/
    Loopstructor.AutoPlayer.Manager.exe 内部 Manager 入口
    Loopstructor.AutoPlayer.Updater.exe 内部 Updater 入口
    System.Windows.Forms.dll            Manager/Updater 共用运行时文件
  updater/
    Loopstructor.AutoPlayer.Updater.dll 旧版包结构兼容标记
  payload/
    bepinex/
    plugin/
  autoplayer-release.json
  version.json
  checksums.sha256
```

固定目录无需随版本升级而重命名。完整解压后运行根部 EXE 无需安装系统 .NET；根启动器为自包含单文件，内部 Manager 与 Updater 都位于 `manager\`，并只携带唯一一套 .NET/WinForms 运行时。`updater\` 只保留供旧版包结构校验使用的小型程序集，不重复携带运行时。更新应用前，Updater 会把自身和共享运行时一起复制到临时目录，因此仍能安全替换整个程序目录。Manager 打开后，标题区会永久显示 `AutoPlayer 版本 v<当前版本>`，不依赖选择或加载游戏目录，更新检查状态也不会覆盖该版本文本；实际版本同时记录在程序根部的 `autoplayer-release.json`。

`payload\bepinex` 必须是经过固定哈希验证的 BepInEx `5.4.23.5` Windows x64 运行时；不得在打包时自动漂移到最新版。`payload\plugin` 只包含 AutoPlayer Plugin、Core 和必要的第三方运行依赖。发布包不得包含 `Assembly-CSharp.dll`、其他游戏 DLL、Unity 测试引用、QA profile、Player.log、状态/截图等测试工件、token 或启动票据；`Assembly-CSharp.dll` 也不得被复制或修改。

## 更新清单

GitHub Release 根资产 `autoplayer-update-manifest.json` 的协议版本为 2：

```json
{
  "schemaVersion": 2,
  "version": "0.1.9",
  "runtimeIdentifier": "win-x64",
  "assetName": "Loopstructor.AutoPlayer-0.1.9-win-x64.zip",
  "sha256": "<64-lowercase-hex>",
  "size": 12345678
}
```

公开仓库且未提供 token 时，更新器不调用匿名 GitHub REST API。它先访问 `https://github.com/<owner>/<repository>/releases/latest`，只接受跳转到同一仓库的精确版本 tag；随后通过该 tag 的 `releases/download/<tag>/...` Release 资产地址下载清单和 ZIP。这样不会消耗匿名 REST API 每个出口 IP 每小时 60 次的配额，也避免在清单下载后继续使用可变化的 `latest` 地址。

提供 token（包括访问私有仓库）时，更新器才调用 GitHub REST API，并使用同一个 API Release 返回的资产 URL。凭据只发送给 `api.github.com`；资产下载跳转到 GitHub Release CDN 后不得转发 `Authorization`。两种路径都不能信任清单中附带的任意下载 URL，并且必须确认精确 Release tag、清单 `version` 和版本化 `assetName` 一致。下载后依次验证：

1. `schemaVersion` 支持；
2. `version` 是比当前版本新的 SemVer；
3. `runtimeIdentifier` 为 `win-x64`；
4. 下载字节数与 `size` 相同；
5. zip SHA-256 与 `sha256` 相同；
6. ZIP 中只有名称和大小写精确为 `Loopstructor 2.AutoPlayer/` 的顶层目录；
7. 安全移除该包装目录后，staging 根存在 `autoplayer-release.json`，其 version 与清单一致；
8. 包内 `checksums.sha256` 全部通过。

验证完成后在 staging 目录解压，退出管理器，再由独立 Updater 替换工具目录。任何验证或替换失败都保留当前可运行版本，不能半更新后继续启动游戏。更新继续使用固定的 `Loopstructor 2.AutoPlayer\` 目录，无需随版本重命名；实际版本以 Manager GUI 和 `autoplayer-release.json` 为准。

清单协议从 schema 1 升级为 schema 2，归档也从扁平结构改为固定包装目录。因此 `v0.1.2` 不能自动升级到 `v0.1.3`，必须手动下载 `Loopstructor.AutoPlayer-0.1.3-win-x64.zip` 并解压；完成这次迁移后，后续新版可以按 schema 2 和同一归档结构自动更新。

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
3. 生成带固定 `Loopstructor 2.AutoPlayer/` 顶层目录的唯一 Release ZIP、SHA-256 与 schema 2 更新清单；
4. 上传未压缩目录作为 workflow artifact，并重新下载验证根部 EXE、marker、checksums 且不存在内嵌产品 ZIP；
5. 创建 GitHub Release，重跑时覆盖同名 assets。

手工触发 Release workflow 只生成 artifact，不自动创建没有对应 tag 的正式 Release。GitHub 下载 artifact 时固定使用外层 ZIP；与 Release ZIP 不同，解开 Actions artifact 后应直接得到扁平的程序文件和根部 Manager EXE，不应出现 `Loopstructor 2.AutoPlayer/` 包装目录或第二层产品 ZIP。

## 仓库与首次发布

Git 仓库和 `origin` 已配置为 [`yingyu4451/gui2`](https://github.com/yingyu4451/gui2)，默认分支为 `main`。Manager 与 Updater 的默认更新源使用相同坐标，Manager 界面不提供仓库地址输入框；旧版 `settings.json` 中的空白坐标会在加载和保存时迁移为该默认值，因此用户无需手工填写。环境变量可在不改包的情况下临时覆盖到测试 fork。

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
git tag v0.1.9
git push origin v0.1.9
```

仅创建本地 tag 不会发布；必须把 tag 推送到已配置的 GitHub remote。

如果仓库保持私有，每台测试机都必须通过进程环境或受控的秘密管理器提供只读 `LOOPSTRUCTOR_AUTOPLAYER_GITHUB_TOKEN`。不要使用 `setx` 永久保存 token，不要把 token 写入 Manager 设置、仓库、发布包或日志；下载重定向到 Release CDN 时也不得携带该 token。公开仓库不需要 token。

## 发布检查表

- Core、Plugin、Manager、Updater 和 Tests 全部在 solution 中且 Release 构建成功；
- 测试覆盖一次性票据、token 拒绝、路径越界拒绝、程序集哈希不符和更新哈希失败；
- 使用新的空 QA profile 分别完成普通模式和随机模式跨波验证；记录 2 波启动、1 波完成、奖励和波后选路，或记录当前版本的新等价证据；
- 验证所有前端写操作只在全局模块和当前场景 Main 稳定就绪后发出；检查随机模式日志并记录转盘离场后的非致命动画异常是否仍存在；
- 确认真实存档目录文件哈希和时间未变化，且 QA profile 独立产生存档；
- 确认四个强制平台写入补丁全部应用；使用 QA 账号或离线环境，不得把“无已知成就写入”等同于账号零痕迹；
- 验证干净的默认防线初始化失败会重试、嵌套污染或已提交动力站点后的失败会要求新进程、路线先于防线、继续 QA 存档不会重建默认防线；
- 验证 Faulted/`NeedsProcessRestart` 后 Manager 禁用 Start 且拒绝向旧游戏进程发送 `start`；
- 在支持构建和未知构建上分别验证通过与 fail-closed；
- 完整解压唯一 Release ZIP，确认它只有固定的 `Loopstructor 2.AutoPlayer\` 顶层目录；进入后验证根启动器无需系统 .NET 即可启动、Manager 与 Updater 共用 `manager\` 内唯一一套运行时、`updater\` 不重复携带运行时、marker 和逐文件 checksums；不得在 ZIP 预览中运行；
- 验证 schema 2 更新清单的资产名、大小和 SHA-256 均指向同一个 Release ZIP，并验证新版 Updater 能安全移除固定包装目录后完成更新；
- 分别验证公开仓库无 token 时不调用匿名 REST API，以及带 token 时只向 `api.github.com` 发送凭据且不向 Release CDN 转发；验证精确 tag、清单版本和 ZIP 资产名不一致时拒绝更新；
- 重新下载 Actions artifact，确认打开后直接是扁平的程序文件和根部 Manager EXE，不含 `Loopstructor 2.AutoPlayer\` 包装目录或第二层产品 ZIP；
- 发布后续版本时，用支持 schema 2 的前一版本执行一次完整自更新与失败回滚测试；`v0.1.2` 到 `v0.1.3` 只验证手动迁移；
- 检查发布包固定使用 BepInEx `5.4.23.5`，且不含 `Assembly-CSharp.dll`、其他游戏 DLL、Unity 测试引用、token、票据、QA 存档、日志、状态或测试截图；
- 发布说明记录游戏构建指纹、程序集哈希、BepInEx `5.4.23.5`、两种模式的验证状态、随机转盘非致命异常、Steam AppID `3841840` 的本机许可限制及账号残余风险；
- GitHub 发布与更新坐标保持为 `yingyu4451/gui2`；若仓库私有，确认测试机通过安全环境提供只读 token，且发布包、日志和 Git 历史均不含 token。

## 升级 BepInEx

升级 BepInEx 时必须手工完成以下步骤：

1. 只从 BepInEx 官方 GitHub Release 选择 Windows x64 的 BepInEx 5 稳定包；
2. 记录精确版本、资产 URL 和 SHA-256；
3. 同时更新 `Directory.Build.props` 中三个 runtime 属性；
4. 检查编译包 API 兼容性；BepInEx runtime `5.4.23.5` 与 NuGet `BepInEx.Core 5.4.21` 的版本差异是已知且有意的；
5. 重跑构建、单元测试、安装、普通启动待机、激活启动和卸载测试；
6. 通过新的项目版本发布，不能静默替换旧 tag 的 BepInEx 载荷。
