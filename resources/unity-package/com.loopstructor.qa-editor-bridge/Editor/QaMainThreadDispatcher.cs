using System;
using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Loopstructor.QA.EditorBridge
{
    internal static class QaMainThreadDispatcher
    {
        private static readonly ConcurrentQueue<QaBridgeWorkItem> m_pending = new ConcurrentQueue<QaBridgeWorkItem>();
        private static bool m_installed;

        /// <summary>注册主线程队列。</summary>
        public static void Install()
        {
            if (m_installed) return;
            m_installed = true;
            EditorApplication.update += Drain;
        }

        /// <summary>解除主线程队列并取消待处理请求。</summary>
        public static void Uninstall()
        {
            if (!m_installed) return;
            m_installed = false;
            EditorApplication.update -= Drain;
            while (m_pending.TryDequeue(out QaBridgeWorkItem item))
                item.Fail(new OperationCanceledException("QA Bridge 已停止。"));
        }

        /// <summary>把 Bridge 请求同步派发到 Unity 主线程。</summary>
        public static JToken Invoke(Func<JToken> callback, int timeoutMilliseconds = 5000)
        {
            QaBridgeWorkItem item = new QaBridgeWorkItem(callback);
            m_pending.Enqueue(item);
            if (!item.Wait(timeoutMilliseconds)) throw new TimeoutException("Unity 主线程未在限定时间内响应 QA 请求。");
            if (item.Error != null) throw item.Error;
            return item.Result;
        }

        /// <summary>每次 Editor 更新最多执行 32 个请求。</summary>
        private static void Drain()
        {
            int processed = 0;
            while (processed < 32 && m_pending.TryDequeue(out QaBridgeWorkItem item))
            {
                item.Execute();
                processed++;
            }
        }
    }
}
