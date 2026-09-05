using System;
using UnityEditor;
using UnityEngine;

namespace Loopstructor.QA.EditorBridge
{
    /// <summary>为无人值守验证提供受限的 Play Mode 入口。</summary>
    public static class QaBridgeBatchMode
    {
        /// <summary>仅在批处理模式下请求进入 Play Mode。</summary>
        public static void EnterPlayModeForVerification()
        {
            if (!Application.isBatchMode)
            {
                throw new InvalidOperationException("该入口仅用于 Unity 批处理验证。");
            }

            EditorApplication.EnterPlaymode();
        }
    }
}
