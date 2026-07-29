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
| Manager/Updater RID | `win-x64`，自包含发布 |

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
.\scripts\package.ps1 -Version 0.1.1
```

已经完成同版本 Release 构建时：

```powershell
.\scripts\package.ps1 -Version 0.1.1 -SkipBuild
```

版本必须是 SemVer。脚本生成：

```text
artifacts/release/
  Loopstructor.AutoPlayer-0.1.1-win-x64.zip
  Loopstructor.AutoPlayer-0.1.1-win-x64.zip.sha256
  autoplayer-update-manifest.json
```

zip 内部结构：

```text
Loopstructor.AutoPlayer.Manager.exe   根目录单文件启动器
manager/                              内部 Manager 运行时及 v0.1.0 更新兼容入口
updater/
payload/
  bepinex/
  plugin/
autoplayer-release.json
version.json
checksums.sha256
```

`payload\bepinex` 必须是经过固定哈希验证的 BepInEx `5.4.23.5` Windows x64 运行时；不得在打包时自动漂移到最新版。`payload\plugin` 只包含 AutoPlayer Plugin、Core 和必要的第三方运行依赖。发布包不得包含 `Assembly-CSharp.dll`、其他游戏 DLL、Unity 测试引用、QA profile、Player.log、状态/截图等测试工件、token 或启动票据；`Assembly-CSharp.dll` 也不得被复制或修改。

## 更新清单

GitHub Release 根资产 `autoplayer-update-manifest.json` 的协议版本为 1：

```json
{
  "schemaVersion": 1,
  "version": "0.1.1",
  "runtimeIdentifier": "win-x64",
  "assetName": "Loopstructor.AutoPlayer-0.1.1-win-x64.zip",
  "sha256": "<64-lowercase-hex>",
  "size": 12345678
}
```

更新器必须从同一个 GitHub Release 的 assets 中按 `assetName` 获取 `browser_download_url`，不能信任清单中附带的任意下载 URL。下载后依次验证：

1. `schemaVersion` 支持；
2. `version` 是比当前版本新的 SemVer；
3. `runtimeIdentifier` 为 `win-x64`；
4. 下载字节数与 `size` 相同；
5. zip SHA-256 与 `sha256` 相同；
6. 解压根存在 `autoplayer-release.json`，其 version 与清单一致；
7. 包内 `checksums.sha256` 全部通过。

验证完成后在 staging 目录解压，退出管理器，再由独立 Updater 替换工具目录。任何验证或替换失败都保留当前可运行版本，不能半更新后继续启动游戏。

## GitHub Actions

`.github/workflows/ci.yml` 在 branch push、pull request 和手工触发时执行：

1. 恢复或 bootstrap 固定 SDK；
2. Release 构建；
3. 测试；
4. 上传 TRX。

`.github/workflows/release.yml` 在 `v*` tag push 时执行：

1. 从 tag 提取 SemVer；
2. 构建和测试；
3. 生成 Windows x64 发布包、SHA-256 与更新清单；
4. 上传未压缩目录作为 workflow artifact，并重新下载验证根部 EXE、marker、checksums 且不存在内嵌产品 ZIP；
5. 创建 GitHub Release，重跑时覆盖同名 assets。

手工触发 Release workflow 只生成 artifact，不自动创建没有对应 tag 的正式 Release。GitHub 下载 artifact 时固定使用外层 ZIP；解开后应直接得到程序文件，不应再出现产品 ZIP。

## 仓库与首次发布

Git 仓库和 `origin` 已配置为 [`yingyu4451/gui2`](https://github.com/yingyu4451/gui2)，默认分支为 `main`。Manager 与 Updater 的默认更新源使用相同坐标；环境变量可在不改包的情况下临时覆盖到测试 fork。

仓库可见性决定客户端认证方式：

- 公开仓库可使用 GitHub Releases 匿名检查更新；
- 私有仓库必须设计 token 的安全存储、最小权限和撤销流程，不能把个人访问令牌编译进程序。

发布前先确认本地提交已经推送到正确远端：

```powershell
git remote -v
git status --short --branch
git fetch origin
```

在 GitHub 仓库 Settings 中允许 GitHub Actions 对 contents 写入，确认 CI 通过后发布：

```powershell
git tag v0.1.1
git push origin v0.1.1
```

仅创建本地 tag 不会发布；必须把 tag 推送到已配置的 GitHub remote。

如果仓库保持私有，每台测试机都必须通过进程环境或受控的秘密管理器提供只读 `LOOPSTRUCTOR_AUTOPLAYER_GITHUB_TOKEN`。不要使用 `setx` 永久保存 token，不要把 token 写入 Manager 设置、仓库、发布包或日志。公开仓库不需要 token。

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
- 解压 zip，验证根启动器、`manager\` 兼容入口、Updater、marker 和逐文件 checksums；确认只需从根目录启动 Manager；
- 用前一版本执行一次完整自更新与失败回滚测试；
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
