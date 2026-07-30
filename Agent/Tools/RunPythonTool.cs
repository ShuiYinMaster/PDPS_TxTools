// TxTools.Agent / Tools / RunPythonTool.cs
// Python(IronPython 2.7) 执行通道。拆成两个工具，因为 ITxAgentTool.IsReadOnly 是类型级属性，
// 无法按调用参数切换审批策略 —— 而 probe 免审批正是这条路径的价值所在。
//
//   probe_python : IsReadOnly=true  -> 免审批。执行后无条件回滚，场景保证不变。
//   run_python   : IsReadOnly=false -> 强制审批 + 审计。成功提交，失败回滚。
//
// 安全边界：PS 2402 的 TxUndoTransactionManager 只有 StartTransaction/EndTransaction/ClearAllTransactions，
//   **没有程序化回滚方法** —— undo 只能把改动分组供用户手动 Ctrl+Z。因此 probe 的只读性由
//   本文件的 ProbeGuard 静态检查承担（拒绝成员赋值与 Set*/Create*/Delete*/Add* 等变更方法），
//   事务包裹退化为兜底：万一有漏网的副作用，用户仍可 Ctrl+Z 撤销整段。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using TxTools.Agent.Core;
using TxTools.Agent.Scripting;

namespace TxTools.Agent.Tools
{
    #region ---------- 共用基类 ----------

    public abstract class PythonToolBase : TxAgentToolBase
    {
        /// <summary>取当前对话 id，供片段固化/归因记录 convId。【统一走 AgentContext.ConvIdProvider】</summary>

        protected abstract PythonRunMode Mode { get; }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""code"": {
                            ""type"": ""string"",
                            ""description"": ""IronPython 2.7 代码（顶层语句，无需包 def/class）。用 print() 输出结果。""
                        }
                    },
                    ""required"": [""code""]
                }");
            }
        }

        public override string Execute(JObject input)
        {
            string code = GetString(input, "code", "");
            if (string.IsNullOrWhiteSpace(code))
                return "错误：code 参数为空。";

            // probe 模式的额外闸门：拦截 undo 覆盖不到的副作用
            if (Mode == PythonRunMode.Probe)
            {
                string blocked = ProbeGuard.Check(code);
                if (blocked != null)
                {
                    return "probe_python 已拒绝执行（场景未被触碰）。\n" + blocked +
                           "\n\nprobe 是只读探测通道。需要修改场景请改用 run_python（会走用户审批）。";
                }
            }

            PythonExecResult result;
            try
            {
                // ITxAgentTool.Execute 已在 PS 主线程上下文中被调用，此处无需再 marshal。
                result = PythonHostProvider.Instance.Run(code, Mode);
            }
            catch (PythonHostException ex)
            {
                return "Python 宿主初始化失败：" + ex.Message +
                       "\n（本次未执行任何代码。）请改用 run_csharp 或现成工具。";
            }
            catch (Exception ex)
            {
                return "Python 执行通道异常：" + ex.GetType().Name + ": " + ex.Message;
            }

            // ── 片段挂钩（与 run_csharp 同一套闭环, lang="python"）──
            // 归因:get_snippet 取出的 python 片段这次用没用成(成败都报)
            SnippetUsageLedger.NoteExecutionAsync(code, result.Success, "python");

            // 固化观察:只看成功的代码,重复出现够次数自动进正式片段
            if (result.Success && Mode == PythonRunMode.Execute)
            {
                string convId = null;
                try { convId = AgentContext.CurrentConvId(); } catch { }
                PendingSnippetStore.ObserveAsync(code, convId, "python");
            }

            return result.ToAgentText();
        }

        /// <summary>两个工具共用的语法与互操作约束说明，避免描述文本两处维护。</summary>
        protected const string CommonConstraints =
            "【语法约束】这是 IronPython 2.7（Python 2.7 方言），不是 Python 3。" +
            "禁止 f-string、类型注解、nonlocal、yield from、async/await、海象运算符 :=。" +
            "宿主已自动注入 from __future__ import division, print_function, unicode_literals，" +
            "因此 1/2 得 0.5、print 是函数、字符串默认 unicode（中文零件名安全）。" +
            "格式化一律用 \"{0}\".format(x)。" +

            "【标准库】只有内建模块可用：sys/clr/time/math/re/itertools/datetime/struct/operator。" +
            "json、os、collections、csv 等需宿主配置 Lib 目录，未配置时预检会拦截；" +
            "改用 .NET 等价物：路径与文件用 System.IO，JSON 用 System.Web.Script.Serialization。" +

            "【.NET 互操作】泛型用方括号：TxObjectList[ITxObject]()，不是尖括号。" +
            "out/ref 参数以元组形式返回。LINQ 扩展方法需先 clr.ImportExtensions(System.Linq)，" +
            "更推荐直接用 Python 列表推导式。重载歧义时用 Method.Overloads[类型...] 显式指定。";
    }

    #endregion

    #region ---------- probe_python ----------

    public sealed class ProbePythonTool : PythonToolBase
    {
        protected override PythonRunMode Mode { get { return PythonRunMode.Probe; } }

        public override string Name { get { return "probe_python"; } }

        public override bool IsReadOnly { get { return true; } }

        public override string Description
        {
            get
            {
                return "【首选探测工具】在 PS 进程内执行 Python 代码，用于**只读探测**。" +
                       "执行前会做静态检查，任何写操作都会被拒绝并原样告知你：" +
                       "对象成员赋值 obj.Prop=x、Set*/Create*/Delete*/Add*/Remove* 等变更类 SDK 方法、" +
                       "文件写入、启动进程。因此本工具免审批，可以放心大胆试错。" +
                       "不确定 SDK 怎么用时，先用它探清楚，再动手 —— 不要靠猜。" +

                       "【三个内置探测函数，这是本工具的核心价值】" +
                       "tx_dir(obj, key=None) 列出对象全部成员及其类型，key 为子串过滤；" +
                       "tx_type(obj) 打印 .NET 完整类型名、继承链与实现的接口；" +
                       "tx_sig(obj, '方法名') 打印该方法的全部重载签名。" +
                       "典型用法：tx_dir(TxApplication.ActiveDocument, 'Selection')。" +

                       "已 from Tecnomatix.Engineering import *，可直接用 TxApplication.ActiveDocument。" +
                       "写顶层语句即可，不需要包 def/class。用 print() 输出。" +
                       "变量在同一对话内跨多次调用保留，可以分步推进。" +

                       CommonConstraints +

                       "【何时不要用它】需要真正提交变更时用 run_python；" +
                       "循环规模超过约 1000 次、或需要建运动学/改拓扑这类结构性变更时，改用 run_csharp 或现成的原生工具。" +
                       "文件读写、启动进程、保存文档等 undo 覆盖不到的操作会被本工具拒绝。";
            }
        }
    }

    #endregion

    #region ---------- run_python ----------

    public sealed class RunPythonTool : PythonToolBase
    {
        protected override PythonRunMode Mode { get { return PythonRunMode.Execute; } }

        public override string Name { get { return "run_python"; } }

        // 关键：非只读 -> 触发强制审批 + 审计。
        public override bool IsReadOnly { get { return false; } }

        public override string Description
        {
            get
            {
                return "在 PS 进程内执行 Python 代码并提交变更。这是会改动场景的操作：" +
                       "执行前需用户确认，整段被包进一次 undo 事务，用户可按一次 Ctrl+Z 撤销全部改动。" +
                       "注意 PS 不支持程序化回滚，脚本中途失败时前面已生效的改动不会自动撤销，" +
                       "需要用户 Ctrl+Z —— 所以务必先探测确认再提交。" +

                       "【标准姿势】先用 probe_python 把 API 探清楚（免审批、零风险），" +
                       "确认无误后再用本工具提交。不要跳过探测直接写变更代码。" +

                       "已 from Tecnomatix.Engineering import *。写顶层语句，用 print() 输出。" +
                       "变量在同一对话内跨多次调用保留。" +

                       CommonConstraints +

                       "【何时改用 run_csharp】循环规模超过约 1000 次（IronPython 比 C# 慢一到两个数量级）；" +
                       "需要直接实例化泛型、传 out 参数、处理显式接口实现；需要创建 WinForms 界面；" +
                       "或本工具连续两次因互操作问题失败。" +
                       "【何时都不该用】已有现成原生工具能做的事，一律优先用现成工具。";
            }
        }
    }

    #endregion

    #region ---------- probe 模式黑名单 ----------

    /// <summary>
    /// probe 的只读保证。
    ///
    /// 原设计是"执行后强制回滚"，但 PS 2402 的 TxUndoTransactionManager 只有
    /// StartTransaction/EndTransaction，没有任何程序化回滚方法 —— undo 只能把改动分组
    /// 供用户手动 Ctrl+Z。所以 probe 的只读性改由这里的**静态检查**承担，
    /// 事务包裹退化为兜底（漏网的副作用用户仍可 Ctrl+Z）。
    ///
    /// 命中即拒绝，不做"猜测意图"的宽容处理。
    /// </summary>
    internal static class ProbeGuard
    {
        private sealed class Rule
        {
            public Regex Pattern;
            public string Why;
            public Rule(string pattern, string why)
            {
                Pattern = new Regex(pattern, RegexOptions.Compiled);
                Why = why;
            }
        }

        /// <summary>undo 覆盖不到的副作用。</summary>
        private static readonly Rule[] SideEffectRules =
        {
            new Rule(@"\bFile\s*\.\s*(Delete|Move|Copy|Create|WriteAll|AppendAll|Open(Write|Append))",
                     "System.IO.File 的写入/删除操作"),
            new Rule(@"\bDirectory\s*\.\s*(Delete|Move|CreateDirectory)",
                     "System.IO.Directory 的写入/删除操作"),
            new Rule(@"\b(StreamWriter|FileStream|BinaryWriter)\s*\(",
                     "文件写入流"),
            new Rule(@"\bos\s*\.\s*(remove|unlink|rmdir|removedirs|rename|system|popen|makedirs|mkdir)",
                     "os 模块的文件系统/进程操作"),
            new Rule(@"\b(shutil|subprocess)\b",
                     "shutil / subprocess 模块"),
            new Rule(@"\bopen\s*\([^)]*['""][waxWAX]",
                     "以写入模式调用 open()"),
            new Rule(@"\bProcess\s*\.\s*Start\b",
                     "启动外部进程"),
            new Rule(@"\bRegistry\w*\s*\.",
                     "注册表访问"),
            new Rule(@"\bTxApplication\s*\.\s*(SaveDocument|CloseDocument|Quit|Exit)\b",
                     "保存/关闭文档（不可撤销）"),
            new Rule(@"\bEnvironment\s*\.\s*Exit\b",
                     "进程退出"),
            // 动态执行:能绕开下面所有基于名字的规则。
            // __import__('os').system('...') 既躲过 \bos\s*\. 也躲过 PascalCase 变更规则,
            // 且变更检查是在剥掉字符串字面量之后跑的 —— exec 里的代码根本不会被看到。
            new Rule(@"\b(exec|eval|compile|__import__)\s*\(",
                     "动态执行 (exec/eval/compile/__import__) —— 静态检查无法看穿"),
            new Rule(@"\bgetattr\s*\([^)]*,\s*['\""](?:Set|Create|Delete|Remove|Add|Insert|Save|Apply|Import|Export)",
                     "用 getattr 动态取变更类方法"),
        };

        /// <summary>
        /// 场景写操作。
        /// 关键判别技巧：.NET SDK 的方法是 PascalCase，Python 内建方法是小写。
        /// 因此只拦大写开头的变更类方法名 —— list.append / dict.update 这类正常的
        /// 探测代码不会被误伤，而 obj.SetItems() / CreateSolidBox() 一定会命中。
        /// </summary>
        private static readonly Rule[] MutationRules =
        {
            new Rule(@"\.\s*[A-Za-z_]\w*\s*(?:\+|-|\*|/|//|%|\|)?=(?!=)",
                     "对对象成员赋值 (obj.Prop = ...)"),
            new Rule(@"\.\s*(?:Set|Create|Delete|Remove|Add|Insert|Move|Modify|Apply|Import|Export|Save|Paste|Cut|Clear|Reset|Attach|Detach|Connect|Disconnect|Rename|Assign|Build|Generate|Write|Load|Replace)[A-Z]\w*\s*\(",
                     "调用变更类 SDK 方法 (Set*/Create*/Delete*/Add*...)"),
            new Rule(@"\.\s*(?:Set|Create|Delete|Remove|Add|Insert|Move|Apply|Save|Clear|Reset|Rename|Update|Execute|Play|Stop)\s*\(",
                     "调用变更类 SDK 方法"),
            new Rule(@"(?m)^\s*del\s+[A-Za-z_]\w*\s*\.",
                     "del 删除对象成员"),
            new Rule(@"\bsetattr\s*\(",
                     "setattr() 动态赋值"),
        };

        /// <summary>命中返回说明文本；未命中返回 null。</summary>
        public static string Check(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;

            // 【必须先剥掉注释和字符串】原来直接在原始代码上跑正则，于是
            //     # obj.Prop = 5              (注释)
            //     print("设置 obj.Name = 新值")  (字符串)
            // 都会命中"对象成员赋值"而被拒。模型收到的拒绝信息说的是
            // "检测到场景写操作"，它不会怀疑到自己的注释头上，只会反复改代码。
            //
            // 两份扫描结果分开用:
            //   noComments —— 去注释、字符串原样。副作用规则要看字符串内容
            //                 (open(path,'w') 的模式字符就在字符串里)。
            //   noStrings  —— 去注释、字符串内容清空。变更规则只认代码结构。
            string noComments, noStrings;
            Scan(code, out noComments, out noStrings);

            var side = Collect(noComments, SideEffectRules);
            var mut = Collect(noStrings, MutationRules);
            if (side.Count == 0 && mut.Count == 0) return null;

            var sb = new StringBuilder();
            if (mut.Count > 0)
            {
                sb.AppendLine("检测到场景写操作（probe 是只读探测，不允许修改场景）：");
                foreach (var h in mut) sb.AppendLine(h);
                sb.AppendLine();
                sb.AppendLine("【已知限制，先看这里再改代码】静态检查分不清"
                    + "「改场景对象」和「改本地 .NET 数据对象」，以下写法虽然是纯内存操作，也会被一并拦下：");
                sb.AppendLine("  - filter.AddEntry(...)      构造 TxTypeFilter");
                sb.AppendLine("  - lst.Add(obj)              往 TxObjectList 里装对象");
                sb.AppendLine("  - data.SomeProp = ...       给 TxXxxCreationData 赋值");
                sb.AppendLine("这不是你写错了，改多少遍都还是会被拦。"
                    + "需要这类构造时直接改用 run_csharp（它走审批，但能做完整遍历），"
                    + "或者先看看有没有现成的原生工具能直接给出结果。");
            }
            if (side.Count > 0)
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.AppendLine("检测到 undo 无法撤销的外部副作用：");
                foreach (var h in side) sb.AppendLine(h);
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// 一次遍历产出两份代码:去注释版、去注释且字符串置空版。
        /// 处理 Python 的三引号、单双引号与反斜杠转义。
        /// 不求做成完整词法分析 —— 只要不再把注释和字符串里的内容当代码看即可。
        /// </summary>
        private static void Scan(string code, out string noComments, out string noStrings)
        {
            var a = new StringBuilder(code.Length);
            var b = new StringBuilder(code.Length);

            int i = 0, n = code.Length;
            while (i < n)
            {
                char c = code[i];

                // 注释:两份都丢弃
                if (c == '#')
                {
                    while (i < n && code[i] != '\n') i++;
                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    bool triple = (i + 2 < n && code[i + 1] == c && code[i + 2] == c);
                    int quoteLen = triple ? 3 : 1;
                    int start = i;
                    i += quoteLen;

                    while (i < n)
                    {
                        if (code[i] == '\\') { i += 2; continue; }
                        if (triple)
                        {
                            if (i + 2 < n && code[i] == c && code[i + 1] == c && code[i + 2] == c)
                            { i += 3; break; }
                        }
                        else
                        {
                            if (code[i] == c) { i++; break; }
                            if (code[i] == '\n') break;   // 未闭合的单行字符串,就此打住
                        }
                        i++;
                    }
                    if (i > n) i = n;

                    a.Append(code, start, i - start);                 // 原样保留
                    b.Append(c, quoteLen).Append(c, quoteLen);        // 置空占位
                    continue;
                }

                a.Append(c);
                b.Append(c);
                i++;
            }

            noComments = a.ToString();
            noStrings = b.ToString();
        }

        private static List<string> Collect(string code, Rule[] rules)
        {
            var hits = new List<string>();
            foreach (var r in rules)
            {
                var m = r.Pattern.Match(code);
                if (m.Success)
                {
                    string s = "- " + r.Why + "（匹配到 \"" + Trim(m.Value) + "\"）";
                    if (!hits.Contains(s)) hits.Add(s);
                }
            }
            return hits;
        }

        private static string Trim(string s)
        {
            s = (s ?? "").Trim();
            return s.Length <= 40 ? s : s.Substring(0, 40) + "…";
        }
    }

    #endregion
}