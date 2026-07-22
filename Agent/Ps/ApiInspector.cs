// TxTools.Agent / Ps / ApiInspector.cs
// 反射式 API 探查：让 AI 从内部读懂 PS SDK 的真实 API，再据此写代码(run_csharp)。
// 思路接你的 DiagnoseApi.cs：搜类型、列成员、探活动对象的成员与取值。

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace TxTools.Agent.Ps
{
    public static class ApiInspector
    {
        private static readonly string[] ObjectMethods = { "ToString", "Equals", "GetHashCode", "GetType" };

        /// <summary>在已加载程序集里搜公共类型名(优先 Tecnomatix)。</summary>
        public static string ListTypes(string keyword, int max)
        {
            if (max <= 0) max = 60;
            var hits = new System.Collections.Generic.List<string>();
            foreach (var asm in OrderedAssemblies())
            {
                foreach (var t in SafeGetTypes(asm))
                {
                    if (t == null || !t.IsPublic) continue;
                    if (string.IsNullOrEmpty(keyword) || t.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                        hits.Add(t.FullName);
                    if (hits.Count >= max) break;
                }
                if (hits.Count >= max) break;
            }
            if (hits.Count == 0) return "没有匹配 \"" + keyword + "\" 的公共类型。";
            var sb = new StringBuilder();
            sb.AppendLine("匹配类型 " + hits.Count + (hits.Count >= max ? "+ (已截断)" : "") + "：");
            foreach (var n in hits) sb.AppendLine("• " + n);
            return sb.ToString();
        }

        /// <summary>列出某类型的公共属性/方法/事件签名。</summary>
        public static string InspectType(string typeName)
        {
            var t = ResolveType(typeName);
            if (t == null) return "未找到类型 " + typeName + "（可先用 list_types 搜名字）。";

            var sb = new StringBuilder();
            sb.AppendLine("类型 " + t.FullName + "  [" + t.Assembly.GetName().Name + "]");
            if (t.BaseType != null) sb.AppendLine("基类: " + t.BaseType.Name);
            var ifaces = t.GetInterfaces();
            if (ifaces.Length > 0) sb.AppendLine("接口: " + string.Join(", ", ifaces.Take(12).Select(i => i.Name).ToArray()));

            var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            var props = t.GetProperties(flags);
            if (props.Length > 0)
            {
                sb.AppendLine("[属性]");
                foreach (var p in props.Take(60))
                    sb.AppendLine("• " + p.Name + " : " + Short(p.PropertyType) +
                                  (p.CanRead ? " get" : "") + (p.CanWrite ? " set" : ""));
            }

            var methods = t.GetMethods(flags)
                .Where(m => !m.IsSpecialName && Array.IndexOf(ObjectMethods, m.Name) < 0)
                .Take(80).ToArray();
            if (methods.Length > 0)
            {
                sb.AppendLine("[方法]");
                foreach (var m in methods)
                {
                    var ps = string.Join(", ", m.GetParameters().Select(x => Short(x.ParameterType)).ToArray());
                    sb.AppendLine("• " + m.Name + "(" + ps + ") : " + Short(m.ReturnType));
                }
            }

            var events = t.GetEvents(flags);
            if (events.Length > 0)
            {
                sb.AppendLine("[事件]");
                foreach (var e in events.Take(20)) sb.AppendLine("• " + e.Name);
            }
            return sb.ToString();
        }

        /// <summary>探查一个活动对象：运行时类型 + 可安全读取的成员值(接 DiagnoseApi)。</summary>
        public static string InspectObjectLive(object obj)
        {
            if (obj == null) return "对象为 null。";
            var t = obj.GetType();
            var sb = new StringBuilder();
            sb.AppendLine("运行时类型: " + t.FullName);
            sb.AppendLine("接口: " + string.Join(", ", t.GetInterfaces().Take(12).Select(i => i.Name).ToArray()));

            sb.AppendLine("[属性取值]");
            int shown = 0;
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                string val;
                try
                {
                    object v = p.GetValue(obj, null);
                    val = v == null ? "null" : (v.GetType().Name + ": " + Trunc(SafeStr(v), 80));
                }
                catch (Exception ex) { val = "[get异常: " + ex.Message.Split('\n')[0] + "]"; }
                sb.AppendLine("• " + p.Name + " = " + val);
                if (++shown >= 50) { sb.AppendLine("…(其余属性省略)"); break; }
            }
            return sb.ToString();
        }

        // ───────── 辅助 ─────────

        private static Type ResolveType(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var t = Type.GetType(name, false, true);
            if (t != null) return t;
            foreach (var asm in OrderedAssemblies())
            {
                try { t = asm.GetType(name, false, true); if (t != null) return t; } catch { }
            }
            // 简单名匹配
            foreach (var asm in OrderedAssemblies())
                foreach (var ct in SafeGetTypes(asm))
                    if (ct != null && (ct.Name == name || ct.FullName == name)) return ct;
            return null;
        }

        private static IEnumerable<Assembly> OrderedAssemblies()
        {
            var all = AppDomain.CurrentDomain.GetAssemblies();
            // Tecnomatix 优先
            return all.OrderByDescending(a =>
            {
                var n = a.GetName().Name ?? "";
                return n.StartsWith("Tecnomatix", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            });
        }

        private static Type[] SafeGetTypes(Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException rtl) { return rtl.Types.Where(x => x != null).ToArray(); }
            catch { return new Type[0]; }
        }

        private static string Short(Type t)
        {
            if (t == null) return "void";
            if (t.IsGenericType)
            {
                var baseName = t.Name.Contains("`") ? t.Name.Substring(0, t.Name.IndexOf('`')) : t.Name;
                var args = string.Join(",", t.GetGenericArguments().Select(Short).ToArray());
                return baseName + "<" + args + ">";
            }
            return t.Name;
        }

        private static string SafeStr(object v)
        {
            try { return v.ToString(); } catch { return "?"; }
        }

        private static string Trunc(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
