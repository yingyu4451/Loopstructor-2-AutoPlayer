# Repository workflow

- 当前产品版本采用 `major.minor.patch`。
- 用户要求修改插件或随插件发布的 Manager、Updater、Launcher 内容时，必须在同一次改动中自动将 `patch` 版本递增 1，并同步 `Directory.Build.props`、`PluginInfo.cs`、演示数据和当前发布文档。
- 纯说明、只读诊断或仅修改测试时不自动递增产品版本。
- 完成用户要求的修改并验证通过后，自动提交并推送到当前 GitHub 远端的当前分支，不再等待单独的提交或推送授权；推送失败时明确报告阻断原因。
- 本次改动递增产品版本时，在版本提交和分支推送成功后，自动创建并推送对应的 `v<major.minor.patch>` Git 标签，用于触发 GitHub Release workflow；已存在同名标签时必须先确认它指向当前版本提交，不覆盖或强制移动错误标签。
- 没有递增产品版本时不自动创建标签或 GitHub Release；除非用户另行要求，也不为纯说明、诊断或测试修改创建标签。
- 协议版本、目录格式和清单 schema 只在接口实际不兼容或格式发生变化时单独调整，不跟随产品版本自动递增。
