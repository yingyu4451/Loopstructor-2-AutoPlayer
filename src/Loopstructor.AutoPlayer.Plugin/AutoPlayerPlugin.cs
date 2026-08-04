using BepInEx;

namespace Loopstructor.AutoPlayer.Plugin;

/// <summary>Bootstraps the process-local runtime in isolated QA or resident player mode.</summary>
[BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
public sealed class AutoPlayerPlugin : BaseUnityPlugin
{
    private bool _activationAccepted;

    private void Awake()
    {
        if (!ActivationContext.TryLoad(Paths.GameRootPath, out ActivationContext? activation, out string activationReason)
            || activation == null)
        {
            Logger.LogInfo("本次启动未激活 AutoPlayer：" + activationReason);
            return;
        }

        _activationAccepted = AutoPlayerRuntimeSession.TryStart(activation, Config, Logger);
    }

    private void OnDestroy()
    {
        if (_activationAccepted && AutoPlayerRuntimeSession.IsRunning)
        {
            Logger.LogInfo("BepInEx 启动组件已退出；独立 AutoPlayer 运行时宿主继续保持本机控制通道。");
        }
    }
}
