#nullable disable

using System;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Loopstructor.QA.EditorBridge
{
    internal sealed class QaBridgeWorkItem
    {
        private readonly Func<JToken> m_callback;
        private readonly ManualResetEventSlim m_completed = new ManualResetEventSlim(false);
        private int m_state;

        public QaBridgeWorkItem(Func<JToken> callback)
        {
            m_callback = callback;
        }

        public JToken Result { get; private set; }
        public Exception Error { get; private set; }

        /// <summary>在 Unity 主线程执行请求。</summary>
        public void Execute()
        {
            if (Interlocked.CompareExchange(ref m_state, 1, 0) != 0) return;
            try
            {
                Result = m_callback();
            }
            catch (Exception exception)
            {
                Error = exception;
            }
            finally
            {
                m_completed.Set();
            }
        }

        /// <summary>让停止中的 Bridge 结束等待。</summary>
        public void Fail(Exception exception)
        {
            if (Interlocked.CompareExchange(ref m_state, 2, 0) != 0) return;
            Error = exception;
            m_completed.Set();
        }

        /// <summary>等待主线程完成请求；超时且尚未执行时原子取消。</summary>
        public bool Wait(int timeoutMilliseconds)
        {
            if (m_completed.Wait(timeoutMilliseconds)) return true;
            Interlocked.CompareExchange(ref m_state, 2, 0);
            return false;
        }
    }
}
