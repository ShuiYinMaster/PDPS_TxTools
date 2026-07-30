// TxTools.Agent / Core / AskUserBridge.cs
//
// ask_user 与 HTML 聊天面板之间的桥。
//
// 为什么不复用 IAgentLoop.AskUserRequest:
//   那个委托签名是 (question, kind, options) -> answer,承载不了
//   default / allow_custom / multiline / fields 这些新参数,
//   而扩签名会同时改动 IAgentLoop、旧 AgentLoop 和 HarnessAgentLoop 三处。
//   这里用一个静态钩子传 JSON 负载,接口一处都不用动。
//
// 【线程约定 —— 非常重要】
//   Handler 会在【后台线程】被调用(ask_user 实现了 ITxOffUiThreadTool)。
//   宿主实现必须:用 BeginInvoke 把"显示"异步投递到 UI 线程,
//   然后在【当前调用线程】上等待结果。
//   绝不能用 Control.Invoke 同步调用后在 UI 线程里等 —— 那会让 UI 线程
//   卡在等待中,用户的点击永远派发不到,整个 PS 冻死。

using System;

namespace TxTools.Agent.Core
{
    public static class AskUserBridge
    {
        /// <summary>
        /// 由 TxAgentForm 在初始化时挂载。
        /// 入参是 JSON 负载(见 BuildPayload 说明),返回用户答复;
        /// 返回 null 表示用户取消。实现方可以抛异常,AskUserTool 会捕获并降级到内置对话框。
        /// </summary>
        public static Func<string, string> Handler;

        public static bool IsAvailable { get { return Handler != null; } }

        /// <summary>
        /// 负载结构(与 chat.html 的 onAskUser 对应):
        /// {
        ///   "question": "...",
        ///   "kind": "confirm|choice|multi_choice|input|form",
        ///   "options": ["A","B"],          // choice / multi_choice
        ///   "default": "A",
        ///   "allowCustom": false,          // choice
        ///   "multiline": false,            // input
        ///   "fields": [                    // kind=form
        ///     { "name":"brand", "label":"品牌", "type":"choice",
        ///       "options":["Fanuc","KUKA"], "default":"KUKA",
        ///       "allowCustom":false, "multiline":false }
        ///   ]
        /// }
        /// 返回值:kind=form 时是 JSON 对象字符串(name -> 答案);其余是纯文本。
        /// </summary>
        public static string Ask(string payloadJson)
        {
            var h = Handler;
            if (h == null) throw new InvalidOperationException("AskUserBridge.Handler 未挂载");
            return h(payloadJson);
        }
    }
}
