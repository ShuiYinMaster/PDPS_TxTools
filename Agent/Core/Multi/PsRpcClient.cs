// TxTools.Agent / Core / Multi / PsRpcClient.cs + 跨环境工具
//
// 主控侧:向指定 PDPS 实例发起工具调用，以及给模型用的三个环境工具。
//
// 典型场景:同一套夹具在两个工作站里各建了一版，要确认它们一致。
// 以前只能两个窗口来回切、肉眼比对;现在一句
// 「比较 站A 和 站B 里 Gun_01 的 TCP」就能出差异表。

using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TxTools.Agent.Core
{
    public static class PsRpcClient
    {
        /// <summary>单次调用超时。PS 主线程可能正忙(仿真、大批量遍历)，给宽松些。</summary>
        public static int TimeoutMs = 120000;

        /// <summary>连接超时。目标进程活着的话连接是瞬间的，卡住多半是它已经僵死。</summary>
        public static int ConnectTimeoutMs = 3000;

        public sealed class Result
        {
            public bool Ok;
            public string Output;
            public string Error;
        }

        public static Result Invoke(PsInstanceInfo target, string tool, JObject input)
        {
            if (target == null) return Fail("目标环境不存在。");

            // 本进程直接走本地注册表，不绕管道 —— 少一次序列化，也避免自连死锁
            if (target.IsSelf) return InvokeLocal(tool, input);

            var req = new JObject
            {
                ["op"] = "invoke",
                ["tool"] = tool,
                ["input"] = input ?? new JObject()
            };

            return Send(target, req);
        }

        public static Result Ping(PsInstanceInfo target)
        {
            if (target == null) return Fail("目标环境不存在。");
            return Send(target, new JObject { ["op"] = "ping" });
        }

        /// <summary>
        /// 快速 ping：给环境枚举/库根识别用 —— 短连接+短读超时，
        /// 目标没响应也尽快返回，不让 UI 卡住。
        /// </summary>
        public static Result PingFast(PsInstanceInfo target, int timeoutMs = 5000)
        {
            if (target == null) return Fail("目标环境不存在。");
            try
            {
                using (var pipe = new NamedPipeClientStream(
                    ".", target.PipeName, PipeDirection.InOut, PipeOptions.None))
                {
                    try { pipe.Connect(Math.Min(ConnectTimeoutMs, 1500)); }
                    catch (TimeoutException)
                    {
                        return Fail("连不上环境 \"" + target.Name + "\"(pid " + target.Pid + ")。");
                    }

                    pipe.ReadMode = PipeTransmissionMode.Message;
                    PsRpcServer.WriteMessage(pipe, "{\"op\":\"ping\"}");

                    var raw = ReadMessageTimed(pipe, timeoutMs, target.Name);
                    if (raw == null)
                        return Fail("环境 \"" + target.Name + "\" 无返回(超时或断连)。");

                    var resp = JObject.Parse(raw);
                    if (resp["ok"] != null && (bool)resp["ok"])
                        return new Result { Ok = true, Output = raw };

                    return Fail((string)resp["error"] ?? "未知错误");
                }
            }
            catch (Exception ex)
            {
                return Fail("环境 \"" + target.Name + "\" ping 失败 - "
                          + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static Result InvokeLocal(string tool, JObject input)
        {
            try
            {
                var reg = LocalToolRegistry;
                ITxAgentTool t;
                if (reg == null || !reg.TryGet(tool, out t) || t == null)
                    return Fail("本环境没有名为 \"" + tool + "\" 的工具。");

                var output = PsContext.Current.Run<string>(
                    delegate { return t.Execute(input ?? new JObject()); });

                return new Result { Ok = true, Output = output ?? "" };
            }
            catch (Exception ex)
            {
                return Fail(ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>本进程的工具注册表。启动时由宿主赋值。</summary>
        public static ToolRegistry LocalToolRegistry { get; set; }

        private static Result Send(PsInstanceInfo target, JObject request)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(
                    ".", target.PipeName, PipeDirection.InOut, PipeOptions.None))
                {
                    try { pipe.Connect(ConnectTimeoutMs); }
                    catch (TimeoutException)
                    {
                        return Fail("连不上环境 \"" + target.Name + "\"(pid " + target.Pid + ")。"
                                  + "该 PDPS 可能已关闭、正忙于阻塞操作，或它的 TxAgent 未启动。");
                    }

                    pipe.ReadMode = PipeTransmissionMode.Message;
                    // 注意：NamedPipe 不支持 ReadTimeout（CanTimeout 恒为 False，设了必抛）。
                    // 读响应超时用 BeginRead + Wait 手动实现。
                    PsRpcServer.WriteMessage(pipe, JsonConvert.SerializeObject(request));

                    var raw = ReadMessageTimed(pipe, TimeoutMs, target.Name);
                    if (raw == null)
                        return Fail("环境 \"" + target.Name + "\" 没有返回内容(连接被中断或响应超时)。");

                    var resp = JObject.Parse(raw);
                    if (resp["ok"] != null && (bool)resp["ok"])
                        return new Result { Ok = true, Output = (string)resp["output"] ?? raw };

                    return Fail((string)resp["error"] ?? "未知错误");
                }
            }
            catch (Exception ex)
            {
                // 响应超时(BeginRead Wait 超时)要单独说清楚,别和连接失败混在一起
                if (ex is System.IO.IOException || ex is TimeoutException)
                    return Fail("环境 \"" + target.Name + "\" 响应超时(超过 " + (TimeoutMs / 1000)
                              + " 秒)。该 PDPS 可能正忙于阻塞操作(仿真/大批量遍历)，稍后重试，"
                              + "或改用只读的轻量工具。");

                return Fail("与环境 \"" + target.Name + "\" 通信失败 - "
                          + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// 带超时读取一帧(4 字节长度前缀 + 正文)。NamedPipe 不支持 ReadTimeout，
        /// 用 BeginRead 异步 + Wait 实现。返回 null 表示超时/断连。
        /// </summary>
        private static string ReadMessageTimed(PipeStream pipe, int timeoutMs, string envName)
        {
            var lenBuf = new byte[4];
            if (!ReadExactTimed(pipe, lenBuf, 4, timeoutMs)) return null;
            int len = BitConverter.ToInt32(lenBuf, 0);
            if (len <= 0 || len > 64 * 1024 * 1024) return null;
            var buf = new byte[len];
            if (!ReadExactTimed(pipe, buf, len, timeoutMs)) return null;
            return Encoding.UTF8.GetString(buf);
        }

        private static bool ReadExactTimed(PipeStream pipe, byte[] buf, int count, int timeoutMs)
        {
            int read = 0;
            while (read < count)
            {
                int n;
                var done = new System.Threading.ManualResetEvent(false);
                IAsyncResult ar = null;
                try { ar = pipe.BeginRead(buf, read, count - read, delegate { try { done.Set(); } catch { } }, null); }
                catch { return false; }
                bool completed = ar != null && done.WaitOne(timeoutMs);
                if (!completed)
                {
                    // 关键修复:超时后不能调用 EndRead —— 它在读取未完成时会阻塞到读完成,
                    // 让"超时"形同虚设(对端活着但不回数据时永久挂起)。
                    // 改为关闭管道,让挂起的 BeginRead 以异常结束,立刻返回超时。
                    try { pipe.Close(); } catch { }
                    return false;
                }
                try { n = pipe.EndRead(ar); }
                catch { return false; }
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }

        private static Result Fail(string msg)
        {
            return new Result { Ok = false, Error = msg };
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  给模型用的三个工具
    // ══════════════════════════════════════════════════════════════

    public sealed class ListEnvironmentsTool : TxAgentToolBase
    {
        public override string Name { get { return "list_environments"; } }

        public override string Description
        {
            get
            {
                return "列出当前打开的所有 PDPS 环境(每个 PDPS 进程算一个环境)，"
                     + "含环境名、打开的 study、是否为当前所在环境。"
                     + "涉及\"另一个窗口\"\"另一个工作站\"\"两边对比\"这类需求时先调它，"
                     + "拿到环境名再用 run_in_environment 或 compare_environments。";
            }
        }

        public override bool IsReadOnly { get { return true; } }

        public override JObject InputSchema
        {
            get { return JObject.Parse("{ \"type\":\"object\", \"properties\":{} }"); }
        }

        public override string Execute(JObject input)
        {
            var live = PsInstanceRegistry.Live();
            if (live.Count == 0) return "没有检测到任何 PDPS 环境。";

            var sb = new StringBuilder();
            sb.AppendLine("当前 " + live.Count + " 个环境:");
            foreach (var i in live)
            {
                sb.Append("  ").Append(i.Name);
                sb.Append("  study=").Append(string.IsNullOrEmpty(i.Study) ? "(未打开)" : i.Study);
                if (i.IsSelf) sb.Append("  ← 当前环境");
                if (i.IsBrain) sb.Append("  [主控]");
                sb.AppendLine();
            }

            if (live.Count == 1)
                sb.Append("只有一个环境，跨环境工具用不上。");
            else
                sb.Append("主被控可互访 —— 写工具也能跨环境执行（在目标进程内运行，可 Ctrl+Z 撤销）。");

            return sb.ToString();
        }
    }

    // ──────────────────────────────────────────────────────────────

    public sealed class RunInEnvironmentTool : TxAgentToolBase, ITxOffUiThreadTool
    {
        public override string Name { get { return "run_in_environment"; } }

        public override string Description
        {
            get
            {
                return "在【指定的另一个 PDPS 环境】里执行一个工具，返回它的输出。"
                     + "先用 list_environments 拿到环境名。"
                     + "tool 传工具名，input 传该工具的参数对象(和直接调用时一样)。"
                     + "【主被控互访】写工具也能跨环境执行 —— 在目标进程内运行，可 Ctrl+Z 撤销。";
            }
        }

        public override bool IsReadOnly { get { return true; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(
                    "{ \"type\":\"object\", \"properties\": {" +
                    " \"environment\": { \"type\":\"string\", \"description\":\"环境名或 pid\" }," +
                    " \"tool\": { \"type\":\"string\", \"description\":\"要执行的工具名\" }," +
                    " \"input\": { \"type\":\"object\", \"description\":\"该工具的参数\" }" +
                    "}, \"required\":[\"environment\",\"tool\"] }");
            }
        }

        public override string Execute(JObject input)
        {
            var envName = GetString(input, "environment");
            var tool = GetString(input, "tool");
            var args = input["input"] as JObject ?? new JObject();

            var target = PsInstanceRegistry.Find(envName);
            if (target == null)
            {
                var live = PsInstanceRegistry.Live().Select(x => x.Name).ToList();
                return "Error: 找不到环境 \"" + envName + "\"。"
                     + (live.Count > 0 ? "当前有:" + string.Join("、", live) : "当前没有可用环境。");
            }

            var r = PsRpcClient.Invoke(target, tool, args);
            if (!r.Ok) return "Error: [" + target.Name + "] " + r.Error;

            return "【" + target.Name + "】\n" + r.Output;
        }
    }

    // ──────────────────────────────────────────────────────────────

    public sealed class CompareEnvironmentsTool : TxAgentToolBase, ITxOffUiThreadTool
    {
        public override string Name { get { return "compare_environments"; } }

        public override string Description
        {
            get
            {
                return "在两个 PDPS 环境里执行【同一个工具、同一套参数】，把两边输出并排返回，"
                     + "并标出逐行差异。"
                     + "典型用途:同一套夹具在两个工作站各建了一版，确认它们是否一致 —— "
                     + "比如比 TCP、关节值、焊枪定义、资源树结构。"
                     + "不传 environment_a 时默认用当前环境。";
            }
        }

        public override bool IsReadOnly { get { return true; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(
                    "{ \"type\":\"object\", \"properties\": {" +
                    " \"environment_a\": { \"type\":\"string\", \"description\":\"留空则为当前环境\" }," +
                    " \"environment_b\": { \"type\":\"string\" }," +
                    " \"tool\": { \"type\":\"string\", \"description\":\"只读工具名\" }," +
                    " \"input\": { \"type\":\"object\", \"description\":\"两边共用的参数\" }," +
                    " \"diff_only\": { \"type\":\"boolean\", \"description\":\"只返回差异行，默认 false\" }" +
                    "}, \"required\":[\"environment_b\",\"tool\"] }");
            }
        }

        public override string Execute(JObject input)
        {
            var aName = GetString(input, "environment_a");
            var bName = GetString(input, "environment_b");
            var tool = GetString(input, "tool");
            var args = input["input"] as JObject ?? new JObject();
            bool diffOnly = input["diff_only"] != null && input["diff_only"].Type == JTokenType.Boolean
                            && (bool)input["diff_only"];

            var a = string.IsNullOrWhiteSpace(aName)
                ? PsInstanceRegistry.Live().FirstOrDefault(x => x.IsSelf)
                : PsInstanceRegistry.Find(aName);
            var b = PsInstanceRegistry.Find(bName);

            if (a == null) return "Error: 找不到环境 A。";
            if (b == null) return "Error: 找不到环境 \"" + bName + "\"。";
            if (a.Pid == b.Pid) return "Error: 两个环境是同一个，无从比较。";

            var ra = PsRpcClient.Invoke(a, tool, args);
            var rb = PsRpcClient.Invoke(b, tool, args);

            var sb = new StringBuilder();
            sb.Append("比较 ").Append(a.Name).Append("  vs  ").Append(b.Name)
              .Append("   工具=").AppendLine(tool);

            if (!ra.Ok) { sb.AppendLine(); sb.Append("[").Append(a.Name).Append("] 失败: ").AppendLine(ra.Error); }
            if (!rb.Ok) { sb.AppendLine(); sb.Append("[").Append(b.Name).Append("] 失败: ").AppendLine(rb.Error); }
            if (!ra.Ok || !rb.Ok) return sb.ToString();

            if (string.Equals(Norm(ra.Output), Norm(rb.Output), StringComparison.Ordinal))
            {
                sb.AppendLine();
                sb.AppendLine("✅ 两边输出完全一致。");
                sb.AppendLine();
                sb.Append(Clip(ra.Output, 2000));
                return sb.ToString();
            }

            sb.AppendLine();
            sb.AppendLine("⚠ 存在差异:");
            sb.AppendLine();
            sb.Append(Diff(ra.Output, rb.Output, a.Name, b.Name, diffOnly));
            return sb.ToString();
        }

        /// <summary>
        /// 逐行对齐比较。不做 LCS —— 这类输出(清单、属性表)通常行序稳定，
        /// 简单对齐已经够用，而 LCS 在几千行上会明显变慢。
        /// </summary>
        private static string Diff(string a, string b, string na, string nb, bool diffOnly)
        {
            var la = (a ?? "").Replace("\r\n", "\n").Split('\n');
            var lb = (b ?? "").Replace("\r\n", "\n").Split('\n');
            int max = Math.Max(la.Length, lb.Length);

            var sb = new StringBuilder();
            int shown = 0, diffs = 0;

            for (int i = 0; i < max; i++)
            {
                var x = i < la.Length ? la[i].TrimEnd() : "(无)";
                var y = i < lb.Length ? lb[i].TrimEnd() : "(无)";
                bool same = string.Equals(x, y, StringComparison.Ordinal);

                if (same)
                {
                    if (diffOnly) continue;
                    if (shown < 200) { sb.Append("  ").AppendLine(x); shown++; }
                    continue;
                }

                diffs++;
                if (shown >= 200) continue;

                sb.Append("- [").Append(na).Append("] ").AppendLine(x);
                sb.Append("+ [").Append(nb).Append("] ").AppendLine(y);
                shown += 2;
            }

            sb.AppendLine();
            sb.Append("共 ").Append(diffs).Append(" 行不同");
            if (shown >= 200) sb.Append("(显示已截断，用 diff_only=true 只看差异)");
            return sb.ToString();
        }

        private static string Norm(string s)
        {
            return (s ?? "").Replace("\r\n", "\n").Trim();
        }

        private static string Clip(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "\n…(已截断)";
        }
    }
}
