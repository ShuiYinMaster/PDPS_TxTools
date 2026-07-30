// TxTools.Agent / Core / Harness / TxAgentToolAdapter.cs
// ITool 适配:把现有的 TxTools.Agent.Core.ITxAgentTool 包成 TxAgent.Core.ITool,
// 从而被新 harness 的 AgentLoop 直接驱动。
//
// 关键五点:
//   1) 不重写任何工具逻辑 —— Execute 内部原样调用 tool.Execute(input)。
//   2) 工具内可能调用 Tecnomatix.Engineering(非线程安全),默认用 host.Invoke 封送回 PS 主线程。
//   3) 【例外】实现 ITxOffUiThreadTool 的工具直接在后台线程跑,绝不封送 ——
//      这类工具会阻塞等待用户交互,封送到主线程会与 UI 消息循环互锁,整个 PS 冻结。
//   4) 识别"内联失败":run_csharp / probe_python 等在编译失败或脚本异常时不抛异常,
//      而是把 "编译失败：" / "== 执行失败 ==" 当正常返回值吐出。不识别的话
//      harness 的错误回灌、连续失败计数、熔断三套机制全部失效。
//   5) 异常信息要留全:只回 ex.Message 会得到 "The method or operation is not implemented."
//      这种无从下手的一行字。这里保留异常类型、内层异常与堆栈头部。

using System;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TxAgent.Core;        // ITool / ToolResult / IAgentHost
using TxTools.Agent.Core; // ITxAgentTool / ITxOffUiThreadTool / GotchasStore / AuditLog

namespace TxTools.Agent.Harness
{
    public sealed class TxAgentToolAdapter : ITool
    {
        private readonly ITxAgentTool _tool;
        private readonly Func<string> _getConvId; // [P3] 供 AutoGotcha 落库时取 convId
        private readonly bool _offUiThread;

        public TxAgentToolAdapter(ITxAgentTool tool, IAgentHost host, Func<string> getConvId = null)
        {
            _tool = tool ?? throw new ArgumentNullException(nameof(tool));
            Host = host;
            _getConvId = getConvId;
            _offUiThread = tool is ITxOffUiThreadTool;
        }

        /// <summary>宿主引用,供 Execute 内做主线程封送。</summary>
        public IAgentHost Host { get; private set; }

        public string Name { get { return _tool.Name; } }

        public string Description { get { return _tool.Description; } }

        public string ParametersJsonSchema
        {
            get
            {
                try
                {
                    return _tool.InputSchema != null
                        ? JsonConvert.SerializeObject(_tool.InputSchema)   // 紧凑 JSON,默认 Formatting.None
                        : "{\"type\":\"object\",\"properties\":{}}";
                }
                catch
                {
                    return "{\"type\":\"object\",\"properties\":{}}";
                }
            }
        }

        /// <summary>旧契约只有 IsReadOnly;写操作即 !IsReadOnly。</summary>
        public bool IsWrite { get { return !_tool.IsReadOnly; } }

        /// <summary>
        /// 旧工具契约没有 destructive 概念。为保持"任何写操作都需用户确认"的既有安全行为,
        /// 把所有写操作都标记为 destructive。
        /// </summary>
        public bool IsDestructive { get { return !_tool.IsReadOnly; } }

        public ToolResult Execute(string argumentsJson, IAgentHost host)
        {
            JObject input;
            try
            {
                // 无参工具有的模型发 ""，有的发 "{}"，都当空对象
                input = string.IsNullOrWhiteSpace(argumentsJson)
                    ? new JObject()
                    : JObject.Parse(argumentsJson);
            }
            catch (Exception ex)
            {
                // 参数 JSON 残缺最常见的原因不是模型写错，而是【输出预算耗尽被截断】——
                // 参数正在流式输出时撞上 max_tokens，就会得到半截 JSON。
                // 报"格式错误"会让模型以为是自己写错了，然后原样重发一遍再次被截断。
                // 所以要能区分出来，并给出正确的处置方式:拆小、少传。
                bool looksTruncated = LooksTruncated(argumentsJson);

                var msg = new StringBuilder();
                msg.Append("工具参数不是合法 JSON: ").AppendLine(ex.Message);
                msg.Append("收到的内容: ").AppendLine(Truncate(argumentsJson, 400));

                if (looksTruncated)
                {
                    msg.AppendLine("【这段 JSON 看起来是被截断的，不是写错了】");
                    msg.AppendLine("多半是参数太长，输出预算在中途耗尽。不要原样重发 —— 会再次被截断。");
                    msg.Append("请把参数改小:代码类工具把脚本拆成几段分次执行；");
                    msg.Append("批量类工具减少一次处理的对象数。");
                }
                else
                {
                    msg.Append("请按 schema 重新构造参数。");
                }

                return ToolResult.Fail("arg", msg.ToString());
            }

            try
            {
                string output;

                if (_offUiThread)
                {
                    // 阻塞等待用户交互的工具:必须留在后台线程,
                    // 否则主线程被 Send 占住,用户的点击永远派发不到 —— UI 直接冻死。
                    output = _tool.Execute(input);
                }
                else
                {
                    // 封送回 PS 主线程执行,期间可安全调用 Tecnomatix.Engineering
                    output = host.Invoke(() => _tool.Execute(input));
                }

                output = output ?? string.Empty;

                // ── 超长输出截断 ──
                // 实测一次 query_scene 返回全场景资源的 ID+名称 JSON 可达 6 万字符(≈3 万 token),
                // 一次就吃掉大半个上下文窗口,后面几轮必然溢出。
                // 这类输出模型真正需要的是"有哪些、大概多少",不是逐条读完 —— 头尾各留一段即可。
                output = ClampOutput(output, _tool.Name);

                // [P3] AutoGotcha: 输出含错误特征时自动落库
                if (IsGotchaWorthy(output) && IsCodeTool(_tool.Name))
                {
                    try
                    {
                        var code = GetStringFromInput(input, "code");
                        var convId = _getConvId != null ? _getConvId() : null;
                        GotchasStore.Record(code, output, convId);
                    }
                    catch { }
                }

                // ── 内联失败识别 ──
                string errorKind = ClassifyInlineFailure(output);
                if (errorKind != null)
                    return ToolResult.Fail(errorKind, output);

                if (_tool.IsReadOnly)
                    return ToolResult.Ok(output);

                return ToolResult.OkMutated(output);
            }
            catch (Exception ex)
            {
                // 不抛出:错误信息回灌给模型自修
                var detail = Describe(ex);
                try { AuditLog.Write("[error] [Harness] 工具 " + _tool.Name + " 抛出异常:\n" + ex); }
                catch { }
                return ToolResult.Fail("host", detail);
            }
        }

        /// <summary>把现有 ToolRegistry 里的全部 ITxAgentTool 包成 ITool,注册进新 harness 的 ToolRegistry。</summary>
        public static TxAgent.Core.ToolRegistry BuildHarnessRegistry(
            TxTools.Agent.Core.ToolRegistry existing, IAgentHost host, Func<string> getConvId = null)
        {
            var reg = new TxAgent.Core.ToolRegistry();
            if (existing != null)
            {
                foreach (var t in existing.Tools)
                {
                    if (t == null) continue;
                    try { reg.Register(new TxAgentToolAdapter(t, host, getConvId)); }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            "[TxAgent.Harness] 包装工具失败,跳过: " + (t.Name ?? "?") + " -> " + ex.Message);
                    }
                }
            }
            return reg;
        }

        /// <summary>
        /// 判断这段 JSON 像不像"被截断"而非"写错"。
        /// 判据:开头合法、括号没配平、且结尾停在一个不该停的位置。
        /// </summary>
        private static bool LooksTruncated(string json)
        {
            if (string.IsNullOrEmpty(json)) return false;

            var t = json.TrimStart();
            if (!t.StartsWith("{") && !t.StartsWith("[")) return false;

            int curly = 0, square = 0;
            bool inStr = false;

            for (int i = 0; i < json.Length; i++)
            {
                var c = json[i];
                if (inStr)
                {
                    if (c == '\\') i++;
                    else if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') { inStr = true; continue; }
                if (c == '{') curly++;
                else if (c == '}') curly--;
                else if (c == '[') square++;
                else if (c == ']') square--;
            }

            // 括号没闭合 或 停在字符串中间 → 截断
            return curly > 0 || square > 0 || inStr;
        }

        // ── 输出截断 ──

        /// <summary>
        /// 单次工具输出允许进入上下文的最大字符数。
        ///
        /// 1M 窗口下可以放宽,但【不要取消】:
        ///   • 长上下文中段召回率明显低于首尾,塞进去不等于用得上;
        ///   • 未命中缓存的输入是 1元/M,一次 6 万字符塞几轮就是实打实的钱。
        /// </summary>
        public static int MaxOutputChars = 50000;

        /// <summary>截断时保留的头部占比,其余留给尾部(结论/统计通常在末尾)。</summary>
        private const double HeadRatio = 0.6;

        private static string ClampOutput(string output, string toolName)
        {
            if (string.IsNullOrEmpty(output)) return output;
            if (MaxOutputChars <= 0 || output.Length <= MaxOutputChars) return output;

            int head = (int)(MaxOutputChars * HeadRatio);
            int tail = MaxOutputChars - head;

            // 尽量切在换行处,避免把一条记录劈成两半
            int headEnd = output.LastIndexOf('\n', Math.Min(head, output.Length - 1));
            if (headEnd < head / 2) headEnd = head;

            int tailStart = output.IndexOf('\n', output.Length - tail);
            if (tailStart < 0 || tailStart > output.Length - tail / 2) tailStart = output.Length - tail;

            int omitted = tailStart - headEnd;

            var sb = new StringBuilder(MaxOutputChars + 400);
            sb.Append(output, 0, headEnd);
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("……【输出过长，中间省略约 " + omitted + " 字符】……");
            sb.AppendLine("完整结果没有进入上下文。若确实需要被省略的部分，请缩小查询范围后重新调用");
            sb.AppendLine("（加类型过滤/名称关键字/父级限定），不要试图让本工具一次返回全部内容。");
            sb.AppendLine();
            sb.Append(output, tailStart, output.Length - tailStart);

            try
            {
                AuditLog.Write("[warn] [Harness] 工具 " + toolName + " 输出 " + output.Length
                    + " 字符，已截断至 " + MaxOutputChars);
            }
            catch { }

            return sb.ToString();
        }

        // ── 异常描述 ──

        private static string Describe(Exception ex)
        {
            var sb = new StringBuilder();
            sb.Append(ex.GetType().Name).Append(": ").AppendLine(ex.Message);

            var inner = ex.InnerException;
            int depth = 0;
            while (inner != null && depth++ < 3)
            {
                sb.Append("  内层异常 -> ").Append(inner.GetType().Name)
                  .Append(": ").AppendLine(inner.Message);
                inner = inner.InnerException;
            }

            var stack = ex.StackTrace;
            if (!string.IsNullOrEmpty(stack))
            {
                sb.AppendLine("  调用栈(前几帧):");
                var lines = stack.Split('\n');
                int take = Math.Min(4, lines.Length);
                for (int i = 0; i < take; i++)
                    sb.Append("    ").AppendLine(lines[i].Trim());
            }

            if (ex is NotImplementedException)
                sb.AppendLine("提示：该功能在宿主侧尚未实现，重试同样调用不会成功。请换用其它工具或参数组合。");

            return sb.ToString();
        }

        // ── 内联失败识别 ──

        /// <summary>
        /// 判断工具虽然正常返回、但内容其实是一次失败。返回 null 表示成功。
        /// 判据必须严格:只认工具自己打出的失败横幅,不认输出正文里偶然出现的异常名。
        /// </summary>
        private static string ClassifyInlineFailure(string output)
        {
            if (string.IsNullOrEmpty(output)) return null;

            if (output.IndexOf("编译失败", StringComparison.Ordinal) >= 0)
                return "compile";

            if (output.IndexOf("== 执行失败 ==", StringComparison.Ordinal) >= 0)
                return "script";

            if (output.StartsWith("执行失败", StringComparison.Ordinal))
                return "script";

            if (output.StartsWith("Error:", StringComparison.Ordinal))
                return "arg";

            return null;
        }

        // ── [P3] AutoGotcha 辅助 ──

        private static bool IsCodeTool(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name == "run_csharp"
                || name == "run_python"
                || name == "probe_python"
                || name == "probe_csharp";
        }

        private static bool IsGotchaWorthy(string output)
        {
            if (string.IsNullOrEmpty(output)) return false;
            if (output.IndexOf("CS0", StringComparison.Ordinal) >= 0) return true;
            if (output.IndexOf("CS1", StringComparison.Ordinal) >= 0) return true;
            if (output.IndexOf("编译失败", StringComparison.Ordinal) >= 0) return true;
            if (output.IndexOf("== 执行失败 ==", StringComparison.Ordinal) >= 0) return true;
            if (output.IndexOf("TxNotImplementedException", StringComparison.Ordinal) >= 0) return true;
            if (output.IndexOf("MissingMemberException", StringComparison.Ordinal) >= 0) return true;
            if (output.IndexOf("MissingMethodException", StringComparison.Ordinal) >= 0) return true;
            if (output.IndexOf("UnboundNameException", StringComparison.Ordinal) >= 0) return true;
            if (output.IndexOf("NullReferenceException", StringComparison.Ordinal) >= 0) return true;
            if (output.IndexOf("未知成员", StringComparison.Ordinal) >= 0) return true;
            if (output.IndexOf("找不到方法", StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private static string GetStringFromInput(JObject input, string key)
        {
            if (input == null) return null;
            var val = input[key];
            if (val == null) return null;
            return val.ToString();
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "…(已截断)";
        }
    }
}
