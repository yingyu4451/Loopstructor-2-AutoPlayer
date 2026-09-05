# Loopstructor 2 QA Tool

面向 Loopstructor 2 的本地 QA 控制域，同时支持已打包 Player 和正在运行的 Unity Editor。

## Language

**Player Target**:
从已验证游戏目录运行的打包游戏进程，可提供自动游玩和完整 QA 调试能力。
_Avoid_: 游戏端、正式端

**Editor Target**:
属于已验证 Unity 工程的 Editor 实例；Edit Mode 只公开工程状态，Play Mode 才接受 QA 调试命令。
_Avoid_: 开发版 Player、编辑器游戏

**Editor Bridge**:
由 QA Tool 管理、使 Unity Editor 能被发现和连接的工程级组件。
_Avoid_: Editor 插件、BepInEx 插件

**Editor Instance**:
一个带独立进程身份和短期心跳的活动 Unity Editor；同一工程可以同时存在多个实例。
_Avoid_: Unity 工程、Editor 会话

**Trusted Connection**:
Host 已交叉确认目标路径、进程身份、实例身份、程序集指纹和本机凭据的连接状态。
_Avoid_: 已发现、在线

**QA Runtime Control**:
只在可信 Player 或 Editor Play Mode 连接上开放的查询与调试写操作集合。
_Avoid_: 作弊开关、远程控制
