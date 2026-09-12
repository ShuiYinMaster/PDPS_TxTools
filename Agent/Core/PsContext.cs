// TxTools.Agent / Core / PsContext.cs
// 把对 Tecnomatix.Engineering 的调用同步路由回 PS 主线程。
// 抽自你 ExportService 的 OnPs(psCtx.Send) 套路——比依赖 async/await 续延更稳：
// 即使某个工具在后台线程被调用，PS API 仍在主线程执行。
// 若已在主线程，WindowsFormsSynchronizationContext.Send 会内联执行，不会死锁。
//
// 【主线程上下文可靠性加固】
//   PsContext.Current 在静态构造(TxAgentService.Start)时可能拿到「哑上下文」——
//   若静态构造早于 PS 消息循环，SynchronizationContext.Current 为 null，
//   就退化成 new SynchronizationContext()，Send 变成后台线程原地执行，
//   Tecnomatix API 就在错误线程跑，native Access Violation 会直接崩掉 PS 进程。
//
//   解决：CaptureFromMainThread() 在【多个主线程入口】持续刷新一个可靠的
//   主线程上下文缓存。任一入口在消息循环运行后执行，就能捕获到真实上下文。
//   Run 时若当前上下文是哑上下文，自动改用缓存中的真实主线程上下文。

using System;
using System.Threading;

namespace TxTools.Agent.Core
{
    public sealed class PsContext
    {
        private static PsContext _current;

        /// <summary>当前进程的主线程路由器。TxAgentCommand 在 PS 主线程启动时设置。</summary>
        public static PsContext Current
        {
            get { return _current ?? (_current = new PsContext(null)); }
            set { _current = value; }
        }

        /// <summary>
        /// 可靠的「主线程上下文」缓存。仅能在 PS 主线程调用，
        /// 把 SynchronizationContext.Current 存下来，供后台线程封送用。
        /// </summary>
        private static volatile SynchronizationContext _mainThreadCtx;

        /// <summary>
        /// 在 PS 主线程调用：捕获当前 SynchronizationContext（若有效则缓存）。
        /// 由静态构造、各命令 Execute、窗体 OnLoad 等主线程入口持续刷新，
        /// 保证至少有一次在消息循环运行后执行，从而拿到真实上下文。
        ///
        /// 若当前线程 SynchronizationContext 为 null 或哑上下文（静态构造早于消息循环时常见），
        /// 则新建一个 WindowsFormsSynchronizationContext 作为缓存 —— 它会把 Send/Post
        /// 封送到创建它的线程(即 PS 主线程)的消息泵，是 headless 被控端最可靠的主线程
        /// 封送来源。
        ///
        /// 【不调用 SetSynchronizationContext】避免改动 PS 主线程自己的上下文，
        /// 只缓存副本供后台线程封送用。幂等安全。
        /// </summary>
        public static void CaptureFromMainThread()
        {
            try
            {
                var ctx = SynchronizationContext.Current;
                if (ctx != null && ctx.GetType() != typeof(SynchronizationContext))
                {
                    _mainThreadCtx = ctx;   // 真实 WinForms 上下文；基类=哑上下文，忽略
                    return;
                }

                // 当前为 null 或哑上下文 → 新建一个封送回本线程的 WinForms 上下文作为缓存
                _mainThreadCtx = new System.Windows.Forms.WindowsFormsSynchronizationContext();
            }
            catch { }
        }

        /// <summary>当前是否持有可靠的（非哑）主线程上下文。</summary>
        public static bool HasRealMainThreadContext
        {
            get { return _mainThreadCtx != null; }
        }

        private readonly SynchronizationContext _ctx;

        public PsContext(SynchronizationContext ctx)
        {
            // 优先用显式传入的；否则当前线程的；否则退回哑上下文（Run 时会自动换缓存）。
            _ctx = ctx ?? SynchronizationContext.Current ?? new SynchronizationContext();
        }

        /// <summary>取本次封送要用的上下文：哑上下文时自动替换为可靠的主线程缓存。</summary>
        private SynchronizationContext EffectiveContext
        {
            get
            {
                // 已是可靠上下文(非基类)直接用它
                if (_ctx != null && _ctx.GetType() != typeof(SynchronizationContext))
                    return _ctx;
                // 哑上下文 → 用主线程缓存(若有)
                if (_mainThreadCtx != null)
                    return _mainThreadCtx;
                return _ctx ?? new SynchronizationContext();
            }
        }

        /// <summary>能否可靠封送回主线程（有真实上下文可用）。</summary>
        public bool CanMarshal
        {
            get
            {
                try
                {
                    var c = EffectiveContext;
                    return c != null && c.GetType() != typeof(SynchronizationContext);
                }
                catch { return false; }
            }
        }

        public void Run(Action action)
        {
            var ctx = EffectiveContext;
            if (ctx == null || ctx.GetType() == typeof(SynchronizationContext))
                throw new InvalidOperationException(
                    "PsContext 拿不到可靠的 PS 主线程上下文，无法安全执行 PS 操作。"
                    + "请先在某处打开插件窗体(或调用 CaptureFromMainThread)以建立主线程封送。");

            Exception captured = null;
            ctx.Send(delegate(object s)
            {
                try { action(); }
                catch (Exception e) { captured = e; }
            }, null);
            if (captured != null) throw captured;
        }

        public T Run<T>(Func<T> func)
        {
            var ctx = EffectiveContext;
            if (ctx == null || ctx.GetType() == typeof(SynchronizationContext))
                throw new InvalidOperationException(
                    "PsContext 拿不到可靠的 PS 主线程上下文，无法安全执行 PS 操作。"
                    + "请先在某处打开插件窗体(或调用 CaptureFromMainThread)以建立主线程封送。");

            T value = default(T);
            Exception captured = null;
            ctx.Send(delegate(object s)
            {
                try { value = func(); }
                catch (Exception e) { captured = e; }
            }, null);
            if (captured != null) throw captured;
            return value;
        }
    }
}
