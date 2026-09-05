using UnityEditor;

namespace Loopstructor.QA.EditorBridge
{
    [InitializeOnLoad]
    internal static class QaBridgeBootstrap
    {
        private static QaBridgeServer m_server;

        /// <summary>注册 Editor 生命周期并延迟启动 Bridge。</summary>
        static QaBridgeBootstrap()
        {
            RegisterEvents();
            EditorApplication.delayCall += Start;
        }

        /// <summary>集中注册 Editor 生命周期事件。</summary>
        private static void RegisterEvents()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
        }

        /// <summary>根据 Play Mode 切换启动或停止游戏运行模块。</summary>
        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                Start();
                m_server?.StartRuntime();
            }
            else if (state == PlayModeStateChange.ExitingPlayMode)
            {
                m_server?.StopRuntime();
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                Start();
                m_server?.RefreshRegistration();
            }
        }

        /// <summary>启动主线程派发器与本机 Bridge。</summary>
        private static void Start()
        {
            if (m_server != null) return;
            QaMainThreadDispatcher.Install();
            m_server = new QaBridgeServer();
            m_server.Start();
            if (EditorApplication.isPlaying) m_server.StartRuntime();
        }

        /// <summary>停止运行模块、Bridge 与主线程派发器。</summary>
        private static void Stop()
        {
            m_server?.Dispose();
            m_server = null;
            QaMainThreadDispatcher.Uninstall();
        }
    }
}
