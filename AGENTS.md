# Repository workflow

- 当前产品版本采用 `major.minor.patch`。
- 用户要求修改插件或随插件发布的 Manager、Updater、Launcher 内容时，必须在同一次改动中自动将 `patch` 版本递增 1，并同步 `Directory.Build.props`、`PluginInfo.cs`、演示数据和当前发布文档。
- 纯说明、只读诊断或仅修改测试时不自动递增产品版本。
- 产品版本递增不等于发布授权；除非用户明确要求，不自动提交、推送、创建 Git 标签或 GitHub Release。
- 协议版本、目录格式和清单 schema 只在接口实际不兼容或格式发生变化时单独调整，不跟随产品版本自动递增。
