# Codex 项目指令

将本文件复制到其他仓库根目录并命名为 `AGENTS.md`，使用前填写末尾的项目专用占位项。

## 作用范围与仓库工作流

- 优先遵循更高优先级的 system、developer 和 user 指令。
- 修改文件前读取所有适用的 `AGENTS.md`；更深层目录中的 `AGENTS.md` 管理其自身目录。
- 选择实现方案前先检查仓库，优先使用现有 module、interface、命令和约定。
- 工作树不干净时保留与任务无关的用户改动，不撤销或覆盖非本轮创建的工作。
- 修改范围严格限定于用户请求，不添加推测性功能、依赖、迁移或重构。
- 编辑前说明将要修改的文件或区域；编辑后运行能够覆盖变更行为的最小相关验证。
- 除非用户或仓库的项目专用规则明确要求，不自动提交、推送、创建标签、发布 Release 或部署。

## 每轮 Skill 预检

- 每个用户请求开始时，加载并检查 `grill-me`、`grilling`、`find-skills`、`improve-codebase-architecture`、`tdd` 与 `grill-with-docs`；重复的 `grilling` 流程合并为一次。
- 技能适用时完整执行；不适用时判定为“不适用”并继续，不为满足形式而强制提问、搜索技能、生成架构报告、创建领域文档或编写测试。
- 每轮使用 `grilling` 检查一次需求完整性。决策 frontier 为空时不强制提问；存在实质未决问题时，带推荐答案一次询问当前完整 frontier，并等待用户确认。
- 使用 `find-skills` 检查任务是否缺少所需能力或依赖。只在用户要求扩展能力或任务确实缺少必要技能时，才搜索、评估或安装技能。
- 使用 `improve-codebase-architecture` 检查架构影响。只有架构审查、重构或涉及多个 module/seam 的变更，才运行完整扫描、HTML 报告和候选选择流程。
- `improve-codebase-architecture` 需要架构词汇时使用 `codebase-design`，并统一使用 module、interface、implementation、depth、seam、adapter、leverage 和 locality。
- 功能开发和缺陷修复使用 `tdd`。公共 interface 和测试 seam 存在实质选择时先确认，然后以纵向 red-green 切片实施。纯说明、只读诊断、版本同步、文档/配置或仅测试修改不启动新的 TDD 循环。
- 工作形成或修改架构/领域决策时，使用 `grill-with-docs` 和 `domain-modeling`。`CONTEXT.md` 只记录已解决的领域术语；只有决策难以逆转、缺少上下文时令人意外，且确实经过权衡时才创建 ADR。
- 必需技能或依赖不可用时，明确报告；若缺失能力不阻断当前目标，则使用现有指令继续。

## 前端附加规则

- 修改用户可见的 Vue、React、HTML、CSS、布局、动画、文案、交互状态或前端资源时，额外加载并执行 `frontend-design` 与 `web-design-guidelines`。
- 使用 `frontend-design` 在实现前确立针对当前项目的视觉方向，并在实现后进行自我审查。
- 每次前端审查前，获取 `web-design-guidelines` 要求的最新 Web Interface Guidelines。
- 在有代表性的桌面端与移动端尺寸下验证前端工作，并检查受影响流程中存在的默认、hover、active、focus-visible、disabled、loading、empty 和 error 状态。
- 只修改后端或桌面端主进程、Updater 内部实现、测试数据、版本号或文档时，不触发前端技能。

## 项目专用配置

- 构建命令：`<command>`
- 测试命令：`<command>`
- Lint/类型检查命令：`<command>`
- 打包命令：`<command>`
- 版本策略：`<policy>`
- 提交/推送/发布策略：`<policy>`
