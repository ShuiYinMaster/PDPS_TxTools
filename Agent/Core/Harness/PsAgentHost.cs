// TxTools.Agent / Core / Harness / PsAgentHost.cs
// IAgentHost 的 Process Simulate 实现。
// 把"主线程封送 / 审批 / 回滚点 / 日志"这套宿主能力接到 TxAgent.Core 的 harness 上。
//
// ══════════════════════════════════════════════════════════════════
//  线程约定 —— 这是本文件最重要的事
// ══════════════════════════════════════════════════════════════════
//   harness 的 AgentLoop 跑在【线程池线程】上，所有 IAgentHost 方法都从那里被调用。
//   而 Tecnomatix 对象必须在主线程访问 —— 在错误线程上碰它们，
//   抛的是 native 级 Access Violation，CLR 的 try-catch 【接不住】，直接崩掉 PS 进程。
//
//   所以本类的每个方法都要【自己封送】，不能指望调用方。
//   凡是新增会碰 TxApplication / 文档 / 场景对象的代码，一律包在 Invoke 里。
//
//   同理，WinForms 的 MessageBox 在非 UI 线程弹出是未定义行为，也必须封送。

using System;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using Tecnomatix.Engineering;
using TxAgent.Core;            // IAgentHost / HostMode / RestorePoint
using TxTools.Agent.Core;     // PsContext / AuditLog

namespace TxTools.Agent.Harness
{
    public sealed class PsAgentHost : IAgentHost
    {
        private readonly SynchronizationContext _ctx;

        /// <summary>
        /// 构造时是否拿到了真正的 UI 上下文。
        /// false 表示所有封送都会退化成原地执行 —— 这种状态下碰 PS 对象随时可能崩，
        /// 必须让它显形，不能静默。
        /// </summary>
        private readonly bool _hasRealContext;

        /// <summary>
        /// 审批回调: (工具名, 参数) -> 是否放行。由 UI 层(DriverLoop 桥)注入,
        /// 复用现有 HTML 审批弹窗(含 auto_safe / auto_all 模式)。
        /// 未设置时退回 WinForms MessageBox。
        /// </summary>
        public Func<string, JObject, bool> ConfirmRequest;

        /// <summary>变更类工具自动通过白名单(对应旧 AgentOptions.AutoApproveTools)。
        /// 仅作为 ConfirmRequest 未注入时的 fallback。</summary>
        public System.Collections.Generic.HashSet<string> AutoApproveTools { get; private set; }

        public PsAgentHost(SynchronizationContext ctx)
        {
            var real = ctx ?? SynchronizationContext.Current;

            // 【别静默退化】原来是 ?? new SynchronizationContext()，
            // 而基类的 Send 是在【当前线程原地执行】—— 所有封送都变成空操作，
            // 却没有任何征兆，直到某次在后台线程碰 PS 对象把进程崩掉。
            _hasRealContext = real != null;
            _ctx = real ?? new SynchronizationContext();

            if (!_hasRealContext)
            {
                try
                {
                    AuditLog.Write("[error] [PsAgentHost] 构造时拿不到 SynchronizationContext，"
                        + "主线程封送将失效。请确保在 UI 线程上构造本类。");
                }
                catch { }
            }

            AutoApproveTools = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        }

        // ── 主线程封送 ──

        public void Invoke(Action action)
        {
            if (action == null) return;

            // 已经在目标线程上就直接跑。WindowsFormsSynchronizationContext.Send
            // 在同线程时本来也不会死锁，但省一次封送开销，语义也更清楚。
            if (IsOnMainThread) { action(); return; }

            Exception captured = null;
            _ctx.Send(delegate (object s)
            {
                try { action(); }
                catch (Exception e) { captured = e; }
            }, null);

            // 【异常必须带回调用线程】Send 里抛出的异常不会自动传播，
            // 不重抛的话调用方看到的是"什么都没发生"，比报错难查得多。
            if (captured != null) throw captured;
        }

        public T Invoke<T>(Func<T> func)
        {
            if (func == null) return default(T);
            if (IsOnMainThread) return func();

            T value = default(T);
            Exception captured = null;
            _ctx.Send(delegate (object s)
            {
                try { value = func(); }
                catch (Exception e) { captured = e; }
            }, null);

            if (captured != null) throw captured;
            return value;
        }

        private bool IsOnMainThread
        {
            get { return _hasRealContext && ReferenceEquals(SynchronizationContext.Current, _ctx); }
        }

        // ── 运行模式 ──

        /// <summary>
        /// 【封送】内部反射访问 TxApplication，必须在主线程。
        /// </summary>
        public HostMode Mode
        {
            get
            {
                try { return Invoke(() => IsConnectedToServerCore() ? HostMode.Connected : HostMode.Standalone); }
                catch { return HostMode.Standalone; }
            }
        }

        private static bool IsConnectedToServerCore()
        {
            // 在线模式(Teamcenter / eMServer)的回滚走 Undo Checkout;
            // 这里只做尽力探测,失败一律返回 Standalone(本地文件模式)。
            try
            {
                var t = typeof(TxApplication);
                var prop = t.GetProperty("IsTeamcenterConnected") ?? t.GetProperty("TeamcenterConnected");
                if (prop != null && prop.PropertyType == typeof(bool))
                    return (bool)prop.GetValue(null, null);
            }
            catch { }
            return false;
        }

        // ── 用户确认 ──

        public bool Confirm(string title, string detail, bool destructive)
        {
            string name, argsJson;
            ParseConfirmDetail(detail, out name, out argsJson);

            JObject input = null;
            if (!string.IsNullOrEmpty(argsJson))
            {
                try { input = JObject.Parse(argsJson); }
                catch { input = new JObject(); }
            }

            // 委托由 UI 层注入，它内部自己做 BeginInvoke + 在调用线程等待，
            // 这里【不要】再包一层 Invoke —— 那会把 UI 线程占住等自己，直接死锁。
            if (ConfirmRequest != null)
                return ConfirmRequest(name, input);

            // fallback: 原生弹窗。
            // 【必须封送】WinForms 在非 UI 线程弹窗是未定义行为，
            // 而这里的调用方是 harness 的线程池线程。
            var kind = destructive ? "破坏性" : "变更";
            var msg = "助手请求执行一个" + kind + "操作：\n\n" + (detail ?? "") +
                      "\n\n是否允许？";

            try
            {
                return Invoke(() =>
                    MessageBox.Show(msg, "操作确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
                    == DialogResult.Yes);
            }
            catch (Exception ex)
            {
                Log("error", "确认弹窗失败，按拒绝处理: " + ex.Message);
                return false;   // 弹不出来时宁可拒绝，不能默认放行变更操作
            }
        }

        /// <summary>harness 的确认 detail 固定为 "工具：&lt;name&gt;\n参数：&lt;json&gt;" 格式,这里解析出工具名与参数。</summary>
        private static void ParseConfirmDetail(string detail, out string name, out string argsJson)
        {
            name = null;
            argsJson = null;
            if (string.IsNullOrEmpty(detail)) return;
            var lines = detail.Split(new[] { '\n' }, StringSplitOptions.None);
            foreach (var line in lines)
            {
                if (line.StartsWith("工具：")) name = line.Substring(3).Trim();
                else if (line.StartsWith("参数：")) argsJson = line.Substring(3).Trim();
            }
        }

        // ── 回滚点 ──

        /// <summary>
        /// 【封送】整个方法体都在碰 PS 文档对象并触发保存。
        ///
        /// 这是最危险的一处:每次首个写操作前必然走到，
        /// 从线程池线程访问 TxApplication.ActiveDocument 并调 Save()，
        /// 等于每次改场景都在赌进程不崩。
        /// </summary>
        public RestorePoint CreateRestorePoint(string reason)
        {
            try
            {
                return Invoke(() => CreateRestorePointCore());
            }
            catch (Exception ex)
            {
                return RestorePoint.None("保存失败,未建立回滚点: " + ex.Message);
            }
        }

        private RestorePoint CreateRestorePointCore()
        {
            try
            {
                // Standalone: 保存当前工程(PDPS 保存会生成新文件,旧文件进回收站)。
                var t = typeof(TxApplication);
                var docProp = t.GetProperty("ActiveDocument");
                if (docProp == null) return RestorePoint.None("无法获取活动文档,未建立回滚点。");

                var doc = docProp.GetValue(null, null);
                if (doc == null) return RestorePoint.None("当前没有打开的工程,未建立回滚点。");

                var saveMethod = doc.GetType().GetMethod("Save", Type.EmptyTypes);
                if (saveMethod != null) saveMethod.Invoke(doc, null);

                string path = null;
                var pathProp = doc.GetType().GetProperty("Path")
                            ?? doc.GetType().GetProperty("FileName")
                            ?? doc.GetType().GetProperty("Name");
                if (pathProp != null)
                {
                    try { path = pathProp.GetValue(doc, null) as string; } catch { }
                }

                // 已经在主线程里了，直接取核心实现，别再走会二次封送的 Mode 属性
                var mode = IsConnectedToServerCore() ? HostMode.Connected : HostMode.Standalone;

                return new RestorePoint
                {
                    Created = true,
                    Mode = mode,
                    TimeUtc = DateTime.UtcNow,
                    FilePath = path,
                    HowToRollback = string.IsNullOrEmpty(path)
                        ? "已保存当前工程(PDPS Save),可在文件历史/回收站找回旧版本。"
                        : "已保存当前工程: " + path + " (旧版本在回收站/文件历史)"
                };
            }
            catch (Exception ex)
            {
                return RestorePoint.None("保存失败,未建立回滚点: " + ex.Message);
            }
        }

        // ── 日志 ──

        public void Log(string level, string message)
        {
            try { AuditLog.Write("[" + (level ?? "info") + "] " + (message ?? "")); }
            catch { }
            System.Diagnostics.Debug.WriteLine("[TxAgent.Harness:" + (level ?? "info") + "] " + (message ?? ""));
        }
    }
}
