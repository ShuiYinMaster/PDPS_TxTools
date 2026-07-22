// TxTools.Agent / Ps / CSharpRunner.cs
// 进程内编译执行用户(AI)提供的 C# 代码。用 .NET Framework 自带的 CodeDom 编译器(无需额外包)。
//
// 重要约束/风险：
//  - 自带编译器是 C# 5 语法(无字符串插值、无 ?.、无表达式体成员、无 out var)。
//    若要现代 C#，可引用 NuGet 包 Microsoft.CodeDom.Providers.DotNetCompilerPlatform。
//  - 编译出的程序集加载进 AppDomain 后无法卸载(频繁调用会累积)；run_csharp 宜偶发使用。
//  - 这是任意代码执行：调用方(PsBridge.RunCSharp)负责 用户审批 + Undo 包裹 + 审计。
//
// 用户代码作为方法体注入，可直接用 Tecnomatix.Engineering(如 TxApplication.ActiveDocument)，
// 并可调用 log("...") 输出、return 任意对象作为结果。

using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Tecnomatix.Engineering;

namespace TxTools.Agent.Ps
{
    public static class CSharpRunner
    {
        /// <summary>编译用户代码(纯 CPU，不碰 PS，可在后台线程跑)。成功返回程序集，失败返回 null 并给出 error。</summary>
        public static Assembly Compile(string userCode, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(userCode)) { error = "(空代码)"; return null; }

            using (var provider = CodeDomProvider.CreateProvider("CSharp"))
            {
                var cp = new CompilerParameters
                {
                    GenerateInMemory = true,
                    GenerateExecutable = false,
                    TreatWarningsAsErrors = false
                };
                AddReferences(cp);

                var results = provider.CompileAssemblyFromSource(cp, Wrap(userCode));
                if (results.Errors.HasErrors)
                {
                    var sb = new StringBuilder("编译失败：");
                    sb.AppendLine();
                    foreach (CompilerError err in results.Errors)
                    {
                        if (!err.IsWarning)
                        {
                            int displayLine = err.Line - 8;
                            if (displayLine <= 0) displayLine = err.Line;
                            sb.AppendLine("• (" + displayLine + ") " + err.ErrorNumber + ": " + err.ErrorText);
                        }
                    }
                    sb.Append("提示：自带编译器是 C# 5 语法(无字符串插值/?./表达式体)。");

                    // 根据CS错误码追加针对性修复提示
                    var hints = new StringBuilder();
                    foreach (CompilerError err in results.Errors)
                    {
                        if (!err.IsWarning)
                        {
                            if (err.ErrorNumber == "CS0126")
                                hints.AppendLine("• CS0126修复：三元表达式中的null必须显式转型，如 (string)null 或 (object)null");
                            if (err.ErrorNumber == "CS0021")
                                hints.AppendLine("• CS0021修复：TxSelection没有索引器，改用 .GetItems()[0]");
                            if (err.ErrorNumber == "CS1061")
                                hints.AppendLine("• CS1061修复：该类型没有此方法/属性——先用 inspect_type 验证真实API，不要盲猜");
                            if (err.ErrorNumber == "CS0104")
                                hints.AppendLine("• CS0104修复：命名空间冲突——用完整命名空间如 Tecnomatix.Engineering.TxSelection 区分");
                            if (err.ErrorNumber == "CS1513" || err.ErrorNumber == "CS1022")
                                hints.AppendLine("• CS1513/CS1022修复：花括号不配对——检查每个 { 是否有对应 }");
                            if (err.ErrorNumber == "CS0234")
                                hints.AppendLine("• CS0234修复：命名空间中不存在该类型——用 list_types 搜索正确名称");
                        }
                    }
                    if (hints.Length > 0)
                        sb.AppendLine().AppendLine("针对性修复提示：").Append(hints);

                    error = sb.ToString();
                    return null;
                }
                return results.CompiledAssembly;
            }
        }

        /// <summary>执行已编译的脚本(会碰 PS，必须在主线程调用)。</summary>
        public static string Invoke(Assembly assembly, Action<string> log)
        {
            if (assembly == null) return "(无程序集)";
            var type = assembly.GetType("TxAgentDynamic.Script");
            if (type == null) return "内部错误：未生成 Script 类型。";
            var instance = Activator.CreateInstance(type);
            var method = type.GetMethod("Run");

            object ret;
            try { ret = method.Invoke(instance, new object[] { log ?? (Action<string>)(s => { }) }); }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException ?? tie;
                return "运行时异常: " + inner.GetType().Name + " - " + inner.Message;
            }
            return ret == null ? "(无返回值)" : Convert.ToString(ret);
        }

        private static string Wrap(string body)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.Linq;");
            sb.AppendLine("using System.Text;");
            sb.AppendLine("using Tecnomatix.Engineering;");
            sb.AppendLine("namespace TxAgentDynamic {");
            sb.AppendLine("  public class Script {");
            sb.AppendLine("    public object Run(Action<string> log) {");
            sb.AppendLine(body);
            sb.AppendLine("      return null;");
            sb.AppendLine("    }");
            sb.AppendLine("  }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static void AddReferences(CompilerParameters cp)
        {
            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Action<Assembly> add = asm =>
            {
                try
                {
                    if (asm == null || asm.IsDynamic) return;
                    var loc = asm.Location;
                    if (string.IsNullOrEmpty(loc)) return;
                    if (added.Add(loc)) cp.ReferencedAssemblies.Add(loc);
                }
                catch { }
            };

            // 核心
            add(typeof(object).Assembly);              // mscorlib
            add(typeof(Uri).Assembly);                 // System
            add(typeof(Enumerable).Assembly);          // System.Core
            add(typeof(TxApplication).Assembly);       // Tecnomatix.Engineering

            // 所有已加载的 Tecnomatix.* 与 Newtonsoft.Json
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var n = "";
                try { n = asm.GetName().Name ?? ""; } catch { }
                if (n.StartsWith("Tecnomatix", StringComparison.OrdinalIgnoreCase) ||
                    n.Equals("Newtonsoft.Json", StringComparison.OrdinalIgnoreCase))
                    add(asm);
            }
        }
    }
}
