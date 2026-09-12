// TxTools.Agent / Core / Multi / TxAgentService.cs
//
// 无界面的执行器服务。
//
// ── 为什么要把它从窗体里拆出来 ──
//   被控端(执行器)需要的只是"能接收并执行工具调用",和界面毫无关系。
//   原来 RPC 服务跟着 TxAgentForm 初始化一起启动，导致必须打开那个窗口 ——
//   而那个窗口对被控端用户没有任何用处，还会占屏幕、还要等 WebView2 加载。
//
//   拆出来之后:
//     被控端 PDPS 什么都不用点，插件一加载就自动可被主控发现和调用。
//     主控端打开 TxAgent 窗口，正常对话，需要时跨环境取数据。
//
// ── 在哪调用 ──
//   插件入口(TxTools 加载时执行的那段，通常是某个 TxButton/Command 的注册处，
//   或 Connect/OnLoad 之类)加一行:
//
//       TxAgentService.Start(BuildToolRegistry());
//
//   TxAgentForm 打开时不用重复调 —— Start 是幂等的。

using System;
using System.Threading;

namespace TxTools.Agent.Core
{
    public static class TxAgentService
    {
        private static readonly object _sync = new object();
        private static PsRpcServer _server;
        private static Timer _heartbeat;
        private static bool _started;

        /// <summary>本进程当前角色。窗体可以据此决定是显示对话界面还是执行器提示。</summary>
        public static bool IsBrain
        {
            get { return PsInstanceRegistry.IsSelfBrain(); }
        }

        /// <summary>角色变化时触发(例如主控崩了、本进程顶上)。</summary>
        public static event Action<bool> RoleChanged;

        /// <summary>取当前 study 名。由宿主注入 —— Core 层不该直接依赖 Tecnomatix 类型。</summary>
        public static Func<string> StudyNameGetter { get; set; }

        /// <summary>
        /// 启动执行器。幂等 —— 重复调用只会刷新心跳。
        ///
        /// 【每个 PDPS 进程都要调】包括主控自己:这样"本地环境"和"远程环境"
        /// 走同一条代码路径，不用为本地开分支。
        /// </summary>
        public static void Start(ToolRegistry tools)
        {
            lock (_sync)
            {
                if (_started)
                {
                    PsInstanceRegistry.Heartbeat(Study());
                    return;
                }

                // 【关键】本方法在插件加载的静态构造里执行，此时在 PS 主线程。
                // 被控端用户可能从不点 TxAgent 按钮 —— 若这里不设置，PsContext.Current
                // 的兜底会在管道后台线程捕获到 null SynchronizationContext，
                // 退化成"内联执行"，PS API 就在后台线程跑了（Tecnomatix 非线程安全）。
                // 幂等设置：点按钮的路径（TxAgentCommand.Execute）重复设置无副作用。
                try
                {
                    PsContext.CaptureFromMainThread();   // 刷新可靠主线程上下文缓存
                    if (PsContext.Current == null)
                        PsContext.Current = new PsContext(SynchronizationContext.Current);
                }
                catch { }

                PsRpcClient.LocalToolRegistry = tools;

                // 先注册再起服务:注册决定角色，服务只负责收请求
                var me = PsInstanceRegistry.Register(Study(), wantBrain: true);

                _server = new PsRpcServer(tools, Study);
                _server.Start();

                // 心跳兼角色重判。主控进程崩掉时，剩下的实例会在下一次心跳接管。
                _heartbeat = new Timer(delegate { Tick(); }, null, 30000, 30000);

                _started = true;

                try
                {
                    AuditLog.Write("[info] [TxAgentService] 已启动，角色="
                        + (me.IsBrain ? "主控" : "执行器")
                        + "，环境名=" + me.Name + "，pid=" + PsInstanceRegistry.SelfPid);
                }
                catch { }
            }
        }

        private static bool _lastRole;

        private static void Tick()
        {
            try
            {
                var me = PsInstanceRegistry.Register(Study(), wantBrain: true);
                if (me.IsBrain != _lastRole)
                {
                    _lastRole = me.IsBrain;
                    var h = RoleChanged;
                    if (h != null) { try { h(me.IsBrain); } catch { } }
                }
            }
            catch { }
        }

        public static void Stop()
        {
            lock (_sync)
            {
                if (!_started) return;
                _started = false;

                try { if (_heartbeat != null) _heartbeat.Dispose(); } catch { }
                try { if (_server != null) _server.Dispose(); } catch { }
                try { PsInstanceRegistry.Unregister(); } catch { }

                _heartbeat = null;
                _server = null;
            }
        }

        private static string Study()
        {
            try { return StudyNameGetter != null ? StudyNameGetter() : null; }
            catch { return null; }
        }
    }
}
