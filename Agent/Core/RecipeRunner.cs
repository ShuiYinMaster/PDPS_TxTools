// TxTools.Agent / Core / RecipeRunner.cs
//
// 把配方 + 一组参数绑定，变成一次真实执行。
//
// ── 为什么是"生成前置代码"而不是"字符串替换" ──
//   最省事的做法是把配方代码里的 {{robot}} 替换成对象引用。三个问题:
//   1. 配方代码本身就不再是合法代码了 —— 没法单独跑、没法给 patch_snippet 改、
//      编辑器里全是红波浪线。
//   2. 替换进去的东西如果带引号或反斜杠(中文零件名、路径),会直接破坏语法,
//      而报错指向的是替换后的代码，跟真实原因隔着一层。
//   3. 审批框里给用户看的到底是哪一份?替换前的没有真实参数,替换后的不是配方原文。
//
//   改成在配方代码【前面】拼一段变量声明。配方代码一字不动，
//   审批框展示"前置声明 + 原文"，跑的也正是这个。
//
// ── 绑定失效必须响 ──
//   GetObjectById 拿不到对象时返回 null。如果就这么让配方跑下去，
//   典型表现是几十行之后一个空引用异常，或者更糟 —— 代码里有 if (obj != null) 保护，
//   于是什么都没发生，界面显示"执行完成"。所以每个对象参数后面都跟一句显式抛出。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TxTools.Agent.Core
{
    public sealed class RecipeRunResult
    {
        public bool Ok { get; set; }
        public string Text { get; set; }
        /// <summary>实际执行的完整代码（前置声明 + 配方原文），用于审批展示与审计。</summary>
        public string FullCode { get; set; }
    }

    public static class RecipeRunner
    {
        /// <summary>
        /// 组装完整代码。args 的键是参数名；对象类参数的值是 ITxObject.Id 字符串。
        /// 失败时 error 带上原因，返回 null。
        /// </summary>
        public static string BuildCode(Recipe r, Dictionary<string, string> args, out string error)
        {
            error = null;
            if (r == null) { error = "配方不存在。"; return null; }

            var lang = SnippetStore.NormalizeLang(r.Lang);
            var sb = new StringBuilder();
            var ps = r.Params ?? new List<RecipeParam>();

            foreach (var p in ps)
            {
                string raw = null;
                if (args != null) args.TryGetValue(p.Name, out raw);

                if (string.IsNullOrWhiteSpace(raw))
                {
                    if (!string.IsNullOrWhiteSpace(p.Default)) raw = p.Default;
                    else if (p.Required)
                    {
                        error = "参数 \"" + (p.Label ?? p.Name) + "\" 还没有取值。";
                        return null;
                    }
                    else { AppendNull(sb, lang, p.Name); continue; }
                }

                switch (p.Kind)
                {
                    case "object": AppendObject(sb, lang, p, raw); break;
                    case "objects": AppendObjects(sb, lang, p, raw); break;
                    case "number": if (!AppendNumber(sb, lang, p, raw, ref error)) return null; break;
                    case "bool": AppendBool(sb, lang, p, raw); break;
                    default: AppendText(sb, lang, p, raw); break;
                }
            }

            if (sb.Length > 0)
            {
                sb.AppendLine(lang == "python"
                    ? "# ── 以上为配方参数，以下为配方原文 ──"
                    : "// ── 以上为配方参数，以下为配方原文 ──");
                sb.AppendLine();
            }

            sb.Append(r.Code);
            return sb.ToString();
        }

        // ── 各类参数的代码生成 ──

        private static void AppendObject(StringBuilder sb, string lang, RecipeParam p, string id)
        {
            var lit = Literal(lang, id);
            if (lang == "python")
            {
                sb.AppendLine(p.Name + " = TxApplication.ActiveDocument.GetObjectById(" + lit + ")");
                sb.AppendLine("if " + p.Name + " is None:");
                sb.AppendLine("    raise Exception(" + Literal(lang,
                    "配方参数 " + p.Name + " 绑定的对象不存在（Id=" + id + "）。可能换了 study 或对象已被删除，请在配方栏重新选取。") + ")");
            }
            else
            {
                // 强制转换放在 null 检查之后：先给出"对象没了"这个准确原因，
                // 再让类型不符暴露成 InvalidCastException（也是明确失败）。
                sb.AppendLine("var " + p.Name + "_obj = TxApplication.ActiveDocument.GetObjectById(" + lit + ");");
                sb.AppendLine("if (" + p.Name + "_obj == null) throw new Exception(" + Literal(lang,
                    "配方参数 " + p.Name + " 绑定的对象不存在（Id=" + id + "）。可能换了 study 或对象已被删除，请在配方栏重新选取。") + ");");

                if (!string.IsNullOrWhiteSpace(p.TypeHint))
                    sb.AppendLine("var " + p.Name + " = (" + p.TypeHint.Trim() + ")" + p.Name + "_obj;");
                else
                    sb.AppendLine("var " + p.Name + " = " + p.Name + "_obj;");
            }
        }

        private static void AppendObjects(StringBuilder sb, string lang, RecipeParam p, string ids)
        {
            var list = (ids ?? "").Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                  .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

            // 注意 ITxObject.Id 本身形如 "3,57,2,1"，逗号是 Id 的一部分。
            // 所以多对象绑定在宿主侧用 '|' 分隔后传过来，这里不能按逗号再切。
            list = (ids ?? "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries)
                              .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

            if (lang == "python")
            {
                sb.AppendLine(p.Name + " = TxObjectList[ITxObject]()");
                sb.AppendLine("for _rid in [" + string.Join(", ", list.Select(x => Literal(lang, x))) + "]:");
                sb.AppendLine("    _o = TxApplication.ActiveDocument.GetObjectById(_rid)");
                sb.AppendLine("    if _o is None:");
                sb.AppendLine("        raise Exception(" + Literal(lang, "配方参数 " + p.Name + " 中有对象不存在，Id=")
                    + " + _rid + " + Literal(lang, "。请在配方栏重新选取。") + ")");
                sb.AppendLine("    " + p.Name + ".Add(_o)");
            }
            else
            {
                sb.AppendLine("var " + p.Name + " = new TxObjectList<ITxObject>();");
                sb.AppendLine("foreach (var _rid in new string[] { "
                    + string.Join(", ", list.Select(x => Literal(lang, x))) + " })");
                sb.AppendLine("{");
                sb.AppendLine("    var _o = TxApplication.ActiveDocument.GetObjectById(_rid);");
                sb.AppendLine("    if (_o == null) throw new Exception(" + Literal(lang,
                    "配方参数 " + p.Name + " 中有对象不存在，Id=") + " + _rid + " + Literal(lang,
                    "。请在配方栏重新选取。") + ");");
                sb.AppendLine("    " + p.Name + ".Add(_o);");
                sb.AppendLine("}");
            }
        }

        private static bool AppendNumber(StringBuilder sb, string lang, RecipeParam p,
                                         string raw, ref string error)
        {
            double d;
            if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out d))
            {
                error = "参数 \"" + (p.Label ?? p.Name) + "\" 不是合法数字: " + raw;
                return false;
            }
            // 用不变文化输出，避免中文环境下小数点变成逗号后生成出 1,5 这种代码
            var lit = d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            sb.AppendLine(lang == "python"
                ? p.Name + " = " + lit
                : "var " + p.Name + " = " + lit + ";");
            return true;
        }

        private static void AppendBool(StringBuilder sb, string lang, RecipeParam p, string raw)
        {
            bool b = raw != null && (raw == "1" ||
                     raw.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                     raw.Equals("on", StringComparison.OrdinalIgnoreCase));
            sb.AppendLine(lang == "python"
                ? p.Name + " = " + (b ? "True" : "False")
                : "var " + p.Name + " = " + (b ? "true" : "false") + ";");
        }

        private static void AppendText(StringBuilder sb, string lang, RecipeParam p, string raw)
        {
            sb.AppendLine(lang == "python"
                ? p.Name + " = " + Literal(lang, raw)
                : "var " + p.Name + " = " + Literal(lang, raw) + ";");
        }

        private static void AppendNull(StringBuilder sb, string lang, string name)
        {
            sb.AppendLine(lang == "python" ? name + " = None" : "object " + name + " = null;");
        }

        /// <summary>
        /// 字符串字面量。中文零件名、带反斜杠的路径都要从这里过 ——
        /// 直接拼引号是这类代码生成最经典的炸点。
        /// </summary>
        private static string Literal(string lang, string s)
        {
            s = s ?? "";
            var sb = new StringBuilder("\"");
            foreach (var c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append('"');
            // Python 2.7 下宿主已注入 unicode_literals，普通引号即 unicode，无需 u 前缀
            return sb.ToString();
        }
    }
}
