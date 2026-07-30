// TxTools.Agent / Tools / RunCSharpTool.cs
// 让 AI 根据需求自己写 C# 代码、在 PS 进程内编译执行。这是兜底能力：现成工具搞不定时才用。
//
// 安全：IsReadOnly=false -> 每次执行前强制用户审批(审批框展示完整代码)；包在 Undo 块里(可 Ctrl+Z 撤销)；
//       变更审计写入 audit.log。自带编译器是 C# 5 语法。
//
// v4：新增 Python 执行通道(probe_python / run_python)后，本工具的定位收窄为
//     "性能热点 + 泛型/out 参数密集 + WinForms" 三类。探测类需求一律先走 probe_python。
//
// v5：片段闭环的两个挂钩接在这里 —— 本层同时握着源代码和执行结果，是天然挂载点；
//     PsBridge 是通用 SDK 桥，不该知道片段库的存在。
//       · SnippetUsageLedger.NoteExecutionAsync —— 回填 get_snippet 取出的片段这次用没用成
//       · PendingSnippetStore.ObserveAsync      —— 同类操作重复够次数就固化成正式片段

using Newtonsoft.Json.Linq;
using TxTools.Agent.Core;
using TxTools.Agent.Ps;

namespace TxTools.Agent.Tools
{
    public sealed class RunCSharpTool : TxAgentToolBase
    {
        /// <summary>
        /// 会话 ID 的取法。固化片段时记进 conv_id 便于回溯，取不到也不影响功能。
        /// 【统一走 AgentContext.ConvIdProvider】由宿主在注册工具时注入一次。
        /// </summary>

        public override string Name { get { return "run_csharp"; } }

        public override string Description
        {
            get
            {
                return "当没有合适的现成工具时，写一段 C# 代码在 Process Simulate 进程内执行(兜底能力)。" +
                       "代码作为方法体注入，已 using Tecnomatix.Engineering，可用 TxApplication.ActiveDocument 等；" +
                       "用 log(\"...\") 输出、return 任意对象作为结果。" +
                       "约束：自带编译器是 C# 5 语法(无字符串插值、无 ?.、无表达式体)。" +
                       "这是会改动场景的操作：执行前需用户确认，操作后可 Ctrl+Z 撤销。" +

                       "【何时用本工具，而不是 Python】" +
                       "1) 循环规模超过约 1000 次(IronPython 比 C# 慢一到两个数量级，遍历数千焊点、批量 IK 必须走这里)；" +
                       "2) 需要直接实例化泛型、传 out/ref 参数、处理显式接口实现；" +
                       "3) 需要创建 WinForms 界面；" +
                       "4) run_python 连续两次因 .NET 互操作问题失败。" +

                       "【何时改用 Python】" +
                       "探测 SDK、查询、筛选、串联多个工具这类活，一律先用 probe_python —— " +
                       "它免审批、执行后强制回滚、且有 tx_dir/tx_type/tx_sig 可以直接查出成员和签名，" +
                       "比在这里靠猜 API 再编译试错快得多。" +
                       "本工具连续两次编译失败(尤其是 CS1061/CS0117 这类找不到成员的错误)时，" +
                       "不要继续猜，改用 probe_python 先把 API 查清楚。" +

                       "【务必避免的 C# 5 陷阱】三元 null 必须转型 (string)null；无 $ 插值，用 + 拼接；" +
                       "无 ?. 空条件，用 if 判断；TxSelection 无索引器，用 .GetItems()[0]。" +
                       "写之前先用 list_types / inspect_type / inspect_object 或 probe_python 摸清 API。";
            }
        }

        // 关键：非只读 -> 触发强制审批 + 审计。
        public override bool IsReadOnly { get { return false; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""code"": { ""type"": ""string"", ""description"": ""C# 5 方法体代码。可用 log(string) 输出, return 对象作结果。"" }
                    },
                    ""required"": [""code""]
                }");
            }
        }

        public override string Execute(JObject input)
        {
            var code = GetString(input, "code", "");

            bool success;
            var result = PsBridge.RunCSharp(code, out success);

            // ── 挂钩一：复用归因 ──
            // 【成功失败都要报】只在成功时报，等于只统计好消息，
            // 片段成功率会单调趋近 100%，跟不统计没有区别。
            // 内部是异步的，不占执行路径的时间。
            SnippetUsageLedger.NoteExecutionAsync(code, success, "csharp");

            // ── 挂钩二：片段固化观察 ──
            // 只看成功的代码：跑不通的东西没有固化价值，
            // 而且失败代码进了待定池会污染指纹计数，让错误写法攒够 3 次转正。
            if (success)
            {
                string convId = null;
                try { convId = AgentContext.CurrentConvId(); } catch { }
                PendingSnippetStore.ObserveAsync(code, convId, "csharp");
            }

            return result;
        }
    }
}
