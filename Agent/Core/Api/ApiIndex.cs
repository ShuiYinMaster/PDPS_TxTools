// TxTools.Agent / Core / Api / ApiIndex.cs
// PDPS API 知识库,两层:
//   1) ApiIndex   —— 反射当前进程里已加载的 Tecnomatix 程序集,得到类型/成员/完整签名。
//                    进程内构建一次,不落盘。反射永远和实际 DLL 一致,不存在版本漂移。
//   2) ApiNotes   —— 持久化的"经验注解",记反射看不出来的运行期行为:
//                    setter 会抛异常、方法已废弃、IronPython 下不可用、必须先 XXX 再 YYY 等。
//                    这才是真正值钱、需要跨会话积累的部分。
//
// 设计要点:签名要能直接照抄进代码,所以类型名做了可读化(Int32 -> int,去掉
// Tecnomatix.Engineering. 前缀),泛型/数组/ref/out/可空都还原成 C# 写法。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;

namespace TxTools.Agent.Core
{
    // ────────────────────────────── 数据模型 ──────────────────────────────

    public sealed class ApiMember
    {
        /// <summary>method / property / field / event / ctor</summary>
        public string Kind { get; set; }
        public string Name { get; set; }
        /// <summary>可直接照抄的签名文本。</summary>
        public string Signature { get; set; }
        public bool IsStatic { get; set; }
        public bool Obsolete { get; set; }
        public string ObsoleteMessage { get; set; }
        /// <summary>声明该成员的类型简名。用于区分自有成员与继承成员。</summary>
        public string DeclaredBy { get; set; }
    }

    public sealed class ApiType
    {
        public string Name { get; set; }
        public string FullName { get; set; }
        public string Kind { get; set; }          // class / interface / enum / struct
        public string BaseType { get; set; }
        public List<string> Interfaces { get; set; }
        public List<ApiMember> Members { get; set; }
        public bool Obsolete { get; set; }

        public ApiType()
        {
            Interfaces = new List<string>();
            Members = new List<ApiMember>();
        }
    }

    public sealed class ApiNote
    {
        public string Type { get; set; }
        /// <summary>可选:注解针对的具体成员。留空表示整个类型。</summary>
        public string Member { get; set; }
        public string Text { get; set; }
        /// <summary>gotcha(踩坑) / usage(正确用法) / deprecated(已废弃)</summary>
        public string Kind { get; set; }
        public string ConvId { get; set; }
        public string CreatedUtc { get; set; }
        /// <summary>被检索命中的次数,用于排序,高频注解优先展示。</summary>
        public int HitCount { get; set; }
    }

    // ────────────────────────────── 反射索引 ──────────────────────────────

    public static class ApiIndex
    {
        private static readonly object Sync = new object();
        private static bool _built;

        // 简名 -> 类型(可能重名,故用 List)
        private static readonly Dictionary<string, List<ApiType>> ByShortName =
            new Dictionary<string, List<ApiType>>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, ApiType> ByFullName =
            new Dictionary<string, ApiType>(StringComparer.OrdinalIgnoreCase);

        public static int TypeCount { get { EnsureBuilt(); return ByFullName.Count; } }

        /// <summary>
        /// 从当前 AppDomain 已加载的程序集里挑出 Tecnomatix 相关的来建索引。
        /// PS 插件跑在 PS 进程内,Tecnomatix.Engineering 一定已经加载。
        /// </summary>
        public static void EnsureBuilt()
        {
            if (_built) return;
            lock (Sync)
            {
                if (_built) return;

                var asms = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a =>
                    {
                        try
                        {
                            var n = a.GetName().Name ?? "";
                            return n.StartsWith("Tecnomatix", StringComparison.OrdinalIgnoreCase)
                                || n.StartsWith("TxEu", StringComparison.OrdinalIgnoreCase)
                                || n.StartsWith("Emp", StringComparison.OrdinalIgnoreCase);
                        }
                        catch { return false; }
                    })
                    .ToList();

                foreach (var asm in asms) IndexAssembly(asm);

                _built = true;
                try { AuditLog.Write("[info] [ApiIndex] 已索引类型数: " + ByFullName.Count); }
                catch { }
            }
        }

        /// <summary>强制重建(换了 PS 版本或额外加载了程序集后调用)。</summary>
        public static void Rebuild()
        {
            lock (Sync)
            {
                ByShortName.Clear();
                ByFullName.Clear();
                _built = false;
            }
            EnsureBuilt();
        }

        private static void IndexAssembly(Assembly asm)
        {
            Type[] types;
            try { types = asm.GetExportedTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
            catch { return; }

            foreach (var t in types)
            {
                try
                {
                    var at = Describe(t);
                    if (at == null) continue;

                    ByFullName[at.FullName] = at;

                    List<ApiType> bucket;
                    if (!ByShortName.TryGetValue(at.Name, out bucket))
                    {
                        bucket = new List<ApiType>();
                        ByShortName[at.Name] = bucket;
                    }
                    bucket.Add(at);
                }
                catch { }
            }
        }

        private static ApiType Describe(Type t)
        {
            if (t == null || !t.IsPublic && !t.IsNestedPublic) return null;

            var at = new ApiType
            {
                Name = Pretty(t, false),
                FullName = t.FullName ?? t.Name,
                Kind = t.IsInterface ? "interface" : t.IsEnum ? "enum" : t.IsValueType ? "struct" : "class",
                Obsolete = HasObsolete(t)
            };

            if (t.BaseType != null && t.BaseType != typeof(object))
                at.BaseType = Pretty(t.BaseType, false);

            try
            {
                foreach (var i in t.GetInterfaces())
                    at.Interfaces.Add(Pretty(i, false));
            }
            catch { }

            const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance |
                                       BindingFlags.Static | BindingFlags.FlattenHierarchy;

            if (t.IsEnum)
            {
                foreach (var name in Enum.GetNames(t))
                    at.Members.Add(new ApiMember
                    {
                        Kind = "field",
                        Name = name,
                        Signature = at.Name + "." + name,
                        IsStatic = true,
                        DeclaredBy = at.Name
                    });
                return at;
            }

            try
            {
                foreach (var ctor in t.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
                    at.Members.Add(new ApiMember
                    {
                        Kind = "ctor",
                        Name = at.Name,
                        Signature = "new " + at.Name + "(" + Params(ctor.GetParameters()) + ")",
                        Obsolete = HasObsolete(ctor),
                        ObsoleteMessage = ObsoleteMsg(ctor),
                        DeclaredBy = at.Name
                    });

                foreach (var p in t.GetProperties(Flags))
                {
                    if (p.DeclaringType == typeof(object)) continue;
                    var acc = (p.GetGetMethod() != null ? "get; " : "") + (p.GetSetMethod() != null ? "set; " : "");
                    at.Members.Add(new ApiMember
                    {
                        Kind = "property",
                        Name = p.Name,
                        Signature = Pretty(p.PropertyType, true) + " " + p.Name + " { " + acc + "}",
                        IsStatic = (p.GetGetMethod() ?? p.GetSetMethod())?.IsStatic ?? false,
                        Obsolete = HasObsolete(p),
                        ObsoleteMessage = ObsoleteMsg(p),
                        DeclaredBy = Pretty(p.DeclaringType, false)
                    });
                }

                foreach (var m in t.GetMethods(Flags))
                {
                    if (m.IsSpecialName) continue;                 // 跳过 get_/set_/add_/op_
                    if (m.DeclaringType == typeof(object)) continue;
                    at.Members.Add(new ApiMember
                    {
                        Kind = "method",
                        Name = m.Name,
                        Signature = (m.IsStatic ? "static " : "") + Pretty(m.ReturnType, true) + " " +
                                    m.Name + Generics(m) + "(" + Params(m.GetParameters()) + ")",
                        IsStatic = m.IsStatic,
                        Obsolete = HasObsolete(m),
                        ObsoleteMessage = ObsoleteMsg(m),
                        DeclaredBy = Pretty(m.DeclaringType, false)
                    });
                }

                foreach (var f in t.GetFields(Flags))
                {
                    if (f.DeclaringType == typeof(object)) continue;
                    at.Members.Add(new ApiMember
                    {
                        Kind = "field",
                        Name = f.Name,
                        Signature = (f.IsStatic ? "static " : "") + Pretty(f.FieldType, true) + " " + f.Name,
                        IsStatic = f.IsStatic,
                        Obsolete = HasObsolete(f),
                        DeclaredBy = Pretty(f.DeclaringType, false)
                    });
                }

                foreach (var e in t.GetEvents(Flags))
                {
                    at.Members.Add(new ApiMember
                    {
                        Kind = "event",
                        Name = e.Name,
                        Signature = "event " + Pretty(e.EventHandlerType, true) + " " + e.Name,
                        DeclaredBy = Pretty(e.DeclaringType, false)
                    });
                }
            }
            catch { }

            return at;
        }

        // ── 查询 ──

        /// <summary>按简名或全名查类型。重名时返回多个。</summary>
        public static List<ApiType> Find(string typeName)
        {
            EnsureBuilt();
            if (string.IsNullOrWhiteSpace(typeName)) return new List<ApiType>();
            typeName = typeName.Trim();

            ApiType exact;
            if (ByFullName.TryGetValue(typeName, out exact)) return new List<ApiType> { exact };

            List<ApiType> bucket;
            if (ByShortName.TryGetValue(typeName, out bucket)) return new List<ApiType>(bucket);

            return new List<ApiType>();
        }

        /// <summary>类型名模糊搜索,用于"我不确定叫什么"的场景。</summary>
        public static List<string> SearchTypes(string keyword, int max)
        {
            EnsureBuilt();
            if (string.IsNullOrWhiteSpace(keyword)) return new List<string>();
            keyword = keyword.Trim();

            return ByShortName.Keys
                .Where(k => k.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(k => k.Length)
                .ThenBy(k => k, StringComparer.OrdinalIgnoreCase)
                .Take(max)
                .ToList();
        }

        /// <summary>
        /// 跨类型搜【签名文本】,用于"谁接收/返回某个类型"。
        ///
        /// SearchMembers 只匹配成员名,回答不了"哪个方法吃 TxLibraryData"这类问题 ——
        /// 而找一个数据类的消费者,往往正是摸清一条 API 链路的关键。
        /// </summary>
        public static List<string> SearchSignatures(string keyword, int max)
        {
            EnsureBuilt();
            if (string.IsNullOrWhiteSpace(keyword)) return new List<string>();

            var hits = new List<string>();
            foreach (var t in ByFullName.Values)
            {
                foreach (var m in t.Members)
                {
                    if (m.DeclaredBy != t.Name) continue;      // 只报自有成员
                    if (string.IsNullOrEmpty(m.Signature)) continue;
                    if (m.Signature.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    hits.Add(t.Name + "." + m.Name + "  ->  " + m.Signature);
                    if (hits.Count >= max) return hits;
                }
            }
            return hits;
        }

        /// <summary>跨类型搜成员名,用于"哪个类型上有 AddObject"。</summary>
        public static List<string> SearchMembers(string memberKeyword, int max)
        {
            EnsureBuilt();
            if (string.IsNullOrWhiteSpace(memberKeyword)) return new List<string>();

            var hits = new List<string>();
            foreach (var t in ByFullName.Values)
            {
                foreach (var m in t.Members)
                {
                    if (m.DeclaredBy != t.Name) continue;   // 只报自有成员,避免继承成员刷屏
                    if (m.Name.IndexOf(memberKeyword, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    hits.Add(t.Name + "." + m.Name + "  ->  " + m.Signature);
                    if (hits.Count >= max) return hits;
                }
            }
            return hits;
        }

        // ── 类型名可读化 ──

        private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>
        {
            { "System.Boolean","bool" }, { "System.Byte","byte" }, { "System.SByte","sbyte" },
            { "System.Int16","short" }, { "System.UInt16","ushort" },
            { "System.Int32","int" }, { "System.UInt32","uint" },
            { "System.Int64","long" }, { "System.UInt64","ulong" },
            { "System.Single","float" }, { "System.Double","double" }, { "System.Decimal","decimal" },
            { "System.Char","char" }, { "System.String","string" }, { "System.Object","object" },
            { "System.Void","void" }
        };

        private static string Pretty(Type t, bool allowAlias)
        {
            if (t == null) return "?";

            if (t.IsByRef) return Pretty(t.GetElementType(), allowAlias);
            if (t.IsArray) return Pretty(t.GetElementType(), allowAlias) + "[]";

            var underlying = Nullable.GetUnderlyingType(t);
            if (underlying != null) return Pretty(underlying, allowAlias) + "?";

            if (allowAlias && t.FullName != null)
            {
                string alias;
                if (Aliases.TryGetValue(t.FullName, out alias)) return alias;
            }

            if (t.IsGenericType)
            {
                var name = t.Name;
                int tick = name.IndexOf('`');
                if (tick > 0) name = name.Substring(0, tick);
                var args = t.GetGenericArguments().Select(a => Pretty(a, allowAlias));
                return name + "<" + string.Join(", ", args) + ">";
            }

            return t.Name;
        }

        private static string Generics(MethodInfo m)
        {
            if (!m.IsGenericMethodDefinition) return "";
            return "<" + string.Join(", ", m.GetGenericArguments().Select(a => a.Name)) + ">";
        }

        private static string Params(ParameterInfo[] ps)
        {
            if (ps == null || ps.Length == 0) return "";
            var parts = new List<string>(ps.Length);
            foreach (var p in ps)
            {
                var prefix = "";
                if (p.ParameterType.IsByRef) prefix = p.IsOut ? "out " : "ref ";
                var s = prefix + Pretty(p.ParameterType, true) + " " + p.Name;
                if (p.IsOptional)
                {
                    var dv = p.DefaultValue;
                    s += " = " + (dv == null ? "null" : dv.ToString());
                }
                parts.Add(s);
            }
            return string.Join(", ", parts);
        }

        private static bool HasObsolete(MemberInfo m)
        {
            try { return m.GetCustomAttributes(typeof(ObsoleteAttribute), false).Length > 0; }
            catch { return false; }
        }

        private static string ObsoleteMsg(MemberInfo m)
        {
            try
            {
                var a = m.GetCustomAttributes(typeof(ObsoleteAttribute), false);
                if (a.Length > 0) return ((ObsoleteAttribute)a[0]).Message;
            }
            catch { }
            return null;
        }
    }

    // ────────────────────────────── 经验注解(持久化) ──────────────────────────────

    /// <summary>
    /// 反射看不出来的运行期行为,靠踩坑积累。这是唯一需要落盘的部分。
    /// 存储位置与 SnippetStore / GotchasStore 保持一致的目录习惯。
    /// </summary>
    public static class ApiNotesStore
    {
        private static readonly object Sync = new object();
        private static List<ApiNote> _cache;

        public static string FilePath
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "TxTools", "TxAgent");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "api_notes.json");
            }
        }

        private static List<ApiNote> Load()
        {
            if (_cache != null) return _cache;
            lock (Sync)
            {
                if (_cache != null) return _cache;
                try
                {
                    if (File.Exists(FilePath))
                    {
                        var json = File.ReadAllText(FilePath, Encoding.UTF8);
                        _cache = JsonConvert.DeserializeObject<List<ApiNote>>(json) ?? new List<ApiNote>();
                    }
                    else _cache = new List<ApiNote>();
                }
                catch { _cache = new List<ApiNote>(); }
                return _cache;
            }
        }

        private static void Save()
        {
            lock (Sync)
            {
                try
                {
                    var json = JsonConvert.SerializeObject(_cache ?? new List<ApiNote>(), Formatting.Indented);
                    File.WriteAllText(FilePath, json, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    try { AuditLog.Write("[warn] [ApiNotes] 保存失败: " + ex.Message); } catch { }
                }
            }
        }

        public static List<ApiNote> ForType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return new List<ApiNote>();
            var all = Load();
            lock (Sync)
            {
                return all
                    .Where(n => string.Equals(n.Type, typeName, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(n => n.HitCount)
                    .ToList();
            }
        }

        /// <summary>登记一条注解。同类型+同成员+同文本视为重复,只累加命中数。</summary>
        public static void Record(string type, string member, string text, string kind, string convId)
        {
            if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(text)) return;

            var all = Load();
            lock (Sync)
            {
                var existing = all.FirstOrDefault(n =>
                    string.Equals(n.Type, type, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(n.Member ?? "", member ?? "", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(n.Text, text, StringComparison.Ordinal));

                if (existing != null)
                {
                    existing.HitCount++;
                }
                else
                {
                    all.Add(new ApiNote
                    {
                        Type = type.Trim(),
                        Member = string.IsNullOrWhiteSpace(member) ? null : member.Trim(),
                        Text = text.Trim(),
                        Kind = string.IsNullOrWhiteSpace(kind) ? "gotcha" : kind.Trim(),
                        ConvId = convId,
                        CreatedUtc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                        HitCount = 1
                    });
                }
            }
            Save();
        }

        public static void MarkHit(string typeName)
        {
            var notes = ForType(typeName);
            if (notes.Count == 0) return;
            lock (Sync) { foreach (var n in notes) n.HitCount++; }
            Save();
        }

        public static int Count { get { return Load().Count; } }
    }
}
