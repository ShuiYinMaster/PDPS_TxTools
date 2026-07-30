// TxTools.Agent / Core / MdStore.cs
// 知识类存储的 Markdown 底座:一物一文件。
//
// 为什么从 JSON 换成 MD:
//   • 可读可改 —— snippet 存的是 C# 代码,JSON 里是 "var doc = ...\r\n  foreach ..." 这种
//     转义地狱;MD 里是围栏代码块,能直接看、直接改、直接复制。
//   • Git 友好 —— JSON 里一条记录是一整行长字符串,改一个字符 diff 显示整行重写;
//     MD 逐行 diff,review 时一眼看出改了什么。开源之后这条权重很高。
//   • 并发与体积 —— 写一条只动一个小文件,不用把整份 snippets.json 读-改-写。
//   • 注入省 token —— 内容可原样拼进 prompt,不必反序列化后再格式化。
//
// 各 Store 的公开 API 完全不变,只是底下换了存储介质,
// 所以 MemoryTools / LessonExtractor / AgentLoop 的调用点一行都不用改。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace TxTools.Agent.Core
{
    public static class MdStore
    {
        private static readonly HashSet<string> _migrated =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _sync = new object();

        /// <summary>目录解析:插件目录优先,不可写则回退 LocalAppData —— 与 KeyStore/RecipeStore 一致。</summary>
        public static string FolderPath(string folderName)
        {
            string pluginDir = null;
            try { pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); }
            catch { }

            if (!string.IsNullOrEmpty(pluginDir))
            {
                var d = Path.Combine(pluginDir, "memory", folderName);
                try { Directory.CreateDirectory(d); return d; }
                catch { }
            }

            var local = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TxTools.Agent", "memory", folderName);
            try { Directory.CreateDirectory(local); } catch { }
            return local;
        }

        public static List<MarkdownDoc> LoadAll(string folderName)
        {
            var list = new List<MarkdownDoc>();
            try
            {
                var dir = FolderPath(folderName);
                if (!Directory.Exists(dir)) return list;

                foreach (var f in Directory.GetFiles(dir, "*.md"))
                {
                    var doc = MarkdownDoc.Load(f);
                    if (doc != null) list.Add(doc);
                }
            }
            catch { }
            return list;
        }

        public static bool Write(string folderName, string slug, MarkdownDoc doc)
        {
            if (doc == null || string.IsNullOrWhiteSpace(slug)) return false;
            try { return doc.SaveTo(Path.Combine(FolderPath(folderName), slug + ".md")); }
            catch { return false; }
        }

        public static bool Delete(string folderName, string slug)
        {
            if (string.IsNullOrWhiteSpace(slug)) return false;
            try
            {
                var p = Path.Combine(FolderPath(folderName), slug + ".md");
                if (!File.Exists(p)) return false;
                File.Delete(p);
                return true;
            }
            catch { return false; }
        }

        public static bool IsEmpty(string folderName)
        {
            try
            {
                var dir = FolderPath(folderName);
                return !Directory.Exists(dir) || Directory.GetFiles(dir, "*.md").Length == 0;
            }
            catch { return true; }
        }

        /// <summary>
        /// 一次性迁移:MD 目录为空且能找到旧 JSON 时,把 JSON 逐条转成 MD。
        /// 迁移完把旧文件改名成 *.migrated 而不是删除 —— 出问题还能回去看。
        /// 每个进程每个 folderName 只跑一次。
        /// </summary>
        public static void MigrateOnce(string folderName, string legacyJsonFileName,
                                       Action<string> converter)
        {
            lock (_sync)
            {
                if (_migrated.Contains(folderName)) return;
                _migrated.Add(folderName);
            }

            try
            {
                if (!IsEmpty(folderName)) return;

                var src = FindLegacyJson(legacyJsonFileName);
                if (src == null) return;

                var json = File.ReadAllText(src, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) return;

                converter(json);

                try { File.Move(src, src + ".migrated"); } catch { }

                try
                {
                    AuditLog.Write("[info] [MdStore] 已迁移 " + legacyJsonFileName
                        + " → memory/" + folderName + "/,旧文件改名为 .migrated");
                }
                catch { }
            }
            catch (Exception ex)
            {
                try { AuditLog.Write("[warn] [MdStore] 迁移 " + legacyJsonFileName + " 失败: " + ex.Message); }
                catch { }
            }
        }

        private static string FindLegacyJson(string fileName)
        {
            foreach (var p in LegacyCandidates(fileName))
                if (File.Exists(p)) return p;
            return null;
        }

        private static IEnumerable<string> LegacyCandidates(string fileName)
        {
            string pluginDir = null;
            try { pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); }
            catch { }

            if (!string.IsNullOrEmpty(pluginDir))
                yield return Path.Combine(pluginDir, fileName);

            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TxTools.Agent", fileName);
        }

        /// <summary>同目录下 slug 撞名时补个后缀,避免两条记录互相覆盖。</summary>
        public static string UniqueSlug(string folderName, string slug, string ownerKey)
        {
            if (string.IsNullOrWhiteSpace(slug)) slug = "unnamed";
            try
            {
                var path = Path.Combine(FolderPath(folderName), slug + ".md");
                if (!File.Exists(path)) return slug;

                var existing = MarkdownDoc.Load(path);
                // 就是自己 → 直接覆盖
                if (existing != null && string.Equals(existing.Get("key", ""), ownerKey, StringComparison.Ordinal))
                    return slug;

                var hash = Math.Abs((ownerKey ?? slug).GetHashCode()) % 10000;
                return slug + "_" + hash.ToString("D4");
            }
            catch { return slug; }
        }
    }
}
