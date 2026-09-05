# Loopstructor 2 QA Editor Bridge

本包仅在 Unity Editor 中加载，不进入 Player 构建。Loopstructor 2 QA Tool 负责安装、更新和卸载本包；不要手工修改 `Editor/Managed` 中的工具程序集。

Edit Mode 提供工程、编译、场景和基础目录状态。进入 Play Mode 后，本包启动无 BepInEx/Harmony 依赖的 Editor 运行层，现有 QA 调试命令通过带随机 Bearer token 的回环 HTTP 通道执行。自动游玩和地图自由跳转仅在 Player 插件模式提供。
