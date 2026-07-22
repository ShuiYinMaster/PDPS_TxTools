// TxTools.Agent / Tools / RunCSharpTool.cs
// 让 AI 根据需求自己写 C# 代码、在 PS 进程内编译执行。这是兜底能力：现成工具搞不定时才用。
//
// 安全：IsReadOnly=false -> 每次执行前强制用户审批(审批框展示完整代码)；包在 Undo 块里(可 Ctrl+Z 撤销)；
//       变更审计写入 audit.log。自带编译器是 C# 5 语法。

using Newtonsoft.Json.Linq;
using TxTools.Agent.Core;
using TxTools.Agent.Ps;

namespace TxTools.Agent.Tools
{
    public sealed class RunCSharpTool : TxAgentToolBase
    {
        public override string Name { get { return "run_csharp"; } }

        public override string Description
        {
            get
            {
                return "当没有合适的现成工具时，写一段 C# 代码在 Process Simulate 进程内执行(兜底能力)。" +
                       "代码作为方法体注入，已 using Tecnomatix.Engineering，可用 TxApplication.ActiveDocument 等；" +
                       "用 log(\"...\") 输出、return 任意对象作为结果。" +
                       "约束：自带编译器是 C# 5 语法(无字符串插值、无 ?.、无表达式体)。" +
                       "先用 list_types/inspect_type/inspect_object 摸清 API 再写。" +
                       "这是会改动场景的操作：执行前需用户确认，操作后可 Ctrl+Z 撤销。" +
                       "常见C#5陷阱(务必避免)：三元null必须转型(string)null；无$插值用+拼接；无?.空条件用if判断；TxSelection无索引器用.GetItems()[0]。";
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
            return PsBridge.RunCSharp(GetString(input, "code", ""));
        }
    }
}
