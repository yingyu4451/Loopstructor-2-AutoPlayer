# Repository workflow

- 当前产品版本采用 `major.minor.patch`。
- 用户要求修改插件或随插件发布的 Manager、Updater、Launcher 内容时，必须在同一次改动中自动将 `patch` 版本递增 1，并同步 `Directory.Build.props`、`PluginInfo.cs`、演示数据和当前发布文档。
- 纯说明、只读诊断或仅修改测试时不自动递增产品版本。
- 完成用户要求的修改并验证通过后，自动提交并推送到当前 GitHub 远端的当前分支，不再等待单独的提交或推送授权；推送失败时明确报告阻断原因。
- 本次改动递增产品版本时，在版本提交和分支推送成功后，自动创建并推送对应的 `v<major.minor.patch>` Git 标签，用于触发 GitHub Release workflow；已存在同名标签时必须先确认它指向当前版本提交，不覆盖或强制移动错误标签。
- 没有递增产品版本时不自动创建标签或 GitHub Release；除非用户另行要求，也不为纯说明、诊断或测试修改创建标签。
- 协议版本、目录格式和清单 schema 只在接口实际不兼容或格式发生变化时单独调整，不跟随产品版本自动递增。

## 每轮 Skill 预检

- 每个用户请求开始时，加载并检查 `grill-me`、`grilling`、`find-skills`、`improve-codebase-architecture`、`tdd` 与 `grill-with-docs`；重复的 `grilling` 流程合并为一次。技能适用时完整执行，不适用时判定为“不适用”并继续，不为满足形式而强制提问、搜索技能、生成架构报告或创建测试。
- `grilling` 每轮至少进行一次需求完整性检查；没有实质未决问题时直接继续。`grill-with-docs` 仅在形成或修改架构/领域决策时启用文档建模流程。
- `find-skills` 每轮检查任务所需能力与依赖；仅在用户要求扩展能力或缺少必要技能时搜索、评估或安装，不引入与当前任务无关的技能。
- `tdd` 仅用于功能开发与缺陷修复，并在存在公共 interface 或测试 seam 的实质选择时先确认；纯说明、只读诊断、版本同步、文档/配置或仅测试修改判定为不适用。
- `improve-codebase-architecture` 每轮检查架构影响；仅在架构审查、重构或涉及多个 module/seam 时运行完整扫描、报告和候选选择流程。其所需 `codebase-design` 与 `grill-with-docs` 所需 `domain-modeling` 已安装，适用时一并使用；若未来缺失，明确报告后降级执行可用部分，不阻塞无关任务。
- 涉及用户可见的 Vue、CSS、布局、动画、文案、交互状态或前端资源时，额外加载并执行 `frontend-design` 与 `web-design-guidelines`；纯 Electron 主进程、Updater、测试数据或版本号修改不触发前端技能。
