// TxTools.Agent / Core / PsContext.cs
// 把对 Tecnomatix.Engineering 的调用同步路由回 PS 主线程。
// 抽自你 ExportService 的 OnPs(psCtx.Send) 套路——比依赖 async/await 续延更稳：
// 即使某个工具在后台线程被调用，PS API 仍在主线程执行。
// 若已在主线程，WindowsFormsSynchronizationContext.Send 会内联执行，不会死锁。

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

        private readonly SynchronizationContext _ctx;

        public PsContext(SynchronizationContext ctx)
        {
            _ctx = ctx ?? SynchronizationContext.Current ?? new SynchronizationContext();
        }

        public void Run(Action action)
        {
            Exception captured = null;
            _ctx.Send(delegate(object s)
            {
                try { action(); }
                catch (Exception e) { captured = e; }
            }, null);
            if (captured != null) throw captured;
        }

        public T Run<T>(Func<T> func)
        {
            T value = default(T);
            Exception captured = null;
            _ctx.Send(delegate(object s)
            {
                try { value = func(); }
                catch (Exception e) { captured = e; }
            }, null);
            if (captured != null) throw captured;
            return value;
        }
    }
}
