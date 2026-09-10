// PszPathFixer.cs  --  C# 8.0
// 直接修改 psz 工程文件里的资源路径（远离 SDK 对路径的限制）。
//
// 背景：
//   psz 是标准 ZIP 压缩包，资源路径记录在 StandaloneStudy_PsState.xml 里，
//   格式为 `<fileName>#\相对路径.cojt</fileName>`，其中 `#` = 库根(SystemRootDirectory)占位符。
//   插入含全角路径的组件时，临时把 SystemRootDirectory 指到 junction 链接，
//   于是记录成 `#\ANV_Link\xxx.cojt` 这类相对 junction 的路径。重开项目时 junction
//   可能失效 → 资源掉链接。
//
// 方案：保存后直接改 psz 里的 StandaloneStudy_PsState.xml，
//   把 `#\<junction残留>\组件.cojt` 替换成真实相对路径 `#\2、Resource\...\组件.cojt`，
//   再重载刷新资源树。

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace TxTools.CrossEnvIO
{
    public static class PszPathFixer
    {
        private const string StateFile = "StandaloneStudy_PsState.xml";

        /// <summary>junction 残留目录段（插入时临时建 / 历史残留），改 psz 时要替换成真实路径。</summary>
        private static readonly string[] JunctionSegments =
        {
            "ANV_Link", "GunLink", "CN_ASCII", "CojtLink",
            "Link_", "Link2", "_Link", "_link"
        };

        /// <summary>
        /// 修复 psz 中 StandaloneStudy_PsState.xml 的资源路径：
        ///   把 `#\<junction残留>\组件.cojt` 替换成 `#\真实相对路径\组件.cojt`。
        /// 返回替换次数。libRoot 为当前库根（SystemRootDirectory）。
        /// knownRealPaths：本次重建实际插入过的 cojt 绝对路径。同名的 cojt 在库中
        /// 可能有多份（不同目录），用「实际插入路径」优先解析，避免猜错目录导致
        /// 重载后链接到错误的资源（目录树名对、3D 显示成别的资源）。
        /// </summary>
        public static int FixPszPaths(string pszPath, string libRoot, Action<string> log,
                                      IEnumerable<string> knownRealPaths = null)
        {
            log = log ?? (s => { });
            if (string.IsNullOrWhiteSpace(pszPath) || !File.Exists(pszPath))
            { log("[Psz] 文件不存在: " + pszPath); return 0; }
            if (string.IsNullOrWhiteSpace(libRoot) || !Directory.Exists(libRoot))
            { log("[Psz] 库根无效: " + libRoot); return 0; }

            // 备份
            try
            {
                string bak = pszPath + ".bak";
                File.Copy(pszPath, bak, true);
                log("[Psz] 已备份 → " + bak);
            }
            catch (Exception ex) { log("[Psz] 备份失败: " + ex.Message); }

            string content = null;
            try
            {
                using (var zip = ZipFile.Open(pszPath, ZipArchiveMode.Read))
                {
                    var entry = zip.GetEntry(StateFile);
                    if (entry == null) { log("[Psz] 未找到 " + StateFile); return 0; }
                    using (var reader = new StreamReader(entry.Open(), Encoding.UTF8))
                        content = reader.ReadToEnd();
                }
            }
            catch (Exception ex)
            {
                log("[Psz] 读取失败: " + ex.Message);
                return 0;
            }
            if (string.IsNullOrEmpty(content)) { log("[Psz] 内容为空"); return 0; }

            // 扫描所有 fileName，构建 组件cojt名 → 真实相对路径 映射
            var nameToRealPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var replacements = new List<KeyValuePair<string, string>>();
            int idx = 0;
            while (true)
            {
                int s = content.IndexOf("<fileName>", idx);
                if (s < 0) break;
                int e = content.IndexOf("</fileName>", s);
                if (e < 0) break;
                string path = content.Substring(s + 10, e - (s + 10));
                idx = e + 11;

                // 判断路径是否错误：
                //   ① 含 junction 残留段（ANV_Link/GunLink/CN_ASCII 等）→ 错
                //   ② 相对路径对应文件在库根下不存在（丢失子目录，如 #\F30-xxx.cojt 实应在 1、Product\...\ 下）→ 错
                // 只有路径正确才跳过。
                if (!IsJunctionPath(path) && FileExistsUnderRoot(libRoot, path))
                    continue;

                string cojtName = GetCojtName(path);
                if (string.IsNullOrWhiteSpace(cojtName)) continue;

                string realRel = FindKnownRealRel(libRoot, cojtName, knownRealPaths);
                if (string.IsNullOrEmpty(realRel))
                    realRel = FindRealRelPath(libRoot, cojtName, log);
                if (string.IsNullOrEmpty(realRel)) continue;

                string from = path;
                string to = "#\\" + realRel;
                if (string.Equals(from, to, StringComparison.Ordinal)) continue;

                replacements.Add(new KeyValuePair<string, string>(from, to));
                nameToRealPath[cojtName] = realRel;
            }

            if (replacements.Count == 0)
            {
                log("[Psz] 没有需要修复的 junction 路径（" + StateFile + "）");
                return 0;
            }

            // 执行路径替换
            string newContent = content;
            foreach (var kv in replacements)
                newContent = newContent.Replace("<fileName>" + kv.Key + "</fileName>",
                                                 "<fileName>" + kv.Value + "</fileName>");

            // XML 良构性修复：把所有 <fileName> 内容中的裸 & 转义为 &amp;
            // 根因（2026-08-18 实测）：Sensor&Light 中的 & 在 XML 里是非法 token，
            // PS 用严格解析器，一个良构错误就导致整包无法打开。
            newContent = EscapeXmlInFileNameElements(newContent, log);

            if (string.Equals(newContent, content, StringComparison.Ordinal))
            { log("[Psz] 替换后无变化"); return 0; }

            // 写回 zip
            try
            {
                using (var zip = ZipFile.Open(pszPath, ZipArchiveMode.Update))
                {
                    var entry = zip.GetEntry(StateFile);
                    if (entry != null) entry.Delete();
                    var ne = zip.CreateEntry(StateFile);
                    using (var writer = new StreamWriter(ne.Open(), Encoding.UTF8))
                        writer.Write(newContent);
                }
                foreach (var kv in replacements)
                    log("[Psz] ✓ " + kv.Key + " → " + kv.Value);
                log("[Psz] 已修复 " + replacements.Count + " 条路径 → " + pszPath);
                return replacements.Count;
            }
            catch (Exception ex)
            {
                log("[Psz] 写回失败: " + ex.Message);
                return 0;
            }
        }

        /// <summary>路径是否含 junction 残留段（#\ANV_Link\... 等）。</summary>
        private static bool IsJunctionPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            foreach (var seg in JunctionSegments)
            {
                if (path.IndexOf("\\" + seg, StringComparison.OrdinalIgnoreCase) >= 0
                    || path.IndexOf("/" + seg, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        /// <summary>从 #\...\xxx.cojt 取 cojt 目录名。</summary>
        private static string GetCojtName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            var p = path.TrimStart('#', '\\', '/');
            int i = p.LastIndexOf('\\');
            int j = p.LastIndexOf('/');
            int k = Math.Max(i, j);
            return k >= 0 ? p.Substring(k + 1) : p;
        }

        /// <summary>判断 `#\相对路径` 对应的 cojt 目录在库根下是否存在。</summary>
        private static bool FileExistsUnderRoot(string libRoot, string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return false;
                string rel = path.TrimStart('#').TrimStart('\\', '/').Replace('/', '\\');
                if (string.IsNullOrEmpty(rel)) return false;
                string full = Path.Combine(libRoot.TrimEnd('\\', '/'), rel);
                return Directory.Exists(full);
            }
            catch { return false; }
        }

        /// <summary>
        /// 在库根下递归找 cojt 目录，返回绝对路径（跳过 junction 残留目录）。
        /// 若多个匹配，优先选「段数最多（嵌套最深）」的 —— 真实库通常嵌套深
        /// （如 1、Product\VAN\F30-5400710\xxx.cojt），浅层多为实验残留。
        /// 供 StructureIO 定位待插入 cojt 与 FixPszPaths 修复路径共用。
        /// </summary>
        public static string FindRealCojtInRoot(string libRoot, string cojtName, Action<string> log)
        {
            try
            {
                libRoot = libRoot.TrimEnd('\\', '/');
                string best = null;
                int bestDepth = -1;
                var stack = new Stack<string>();
                stack.Push(libRoot);
                int guard = 0;
                while (stack.Count > 0 && guard++ < 500000)
                {
                    string cur = stack.Pop();
                    if (!Directory.Exists(cur)) continue;

                    if (string.Equals(Path.GetFileName(cur), cojtName, StringComparison.OrdinalIgnoreCase)
                        && !IsJunctionPath(cur))
                    {
                        string rel = cur.Substring(libRoot.Length).TrimStart('\\', '/');
                        int depth = rel.Split('\\', '/').Length;
                        if (depth > bestDepth)
                        {
                            bestDepth = depth;
                            best = cur;
                        }
                    }

                    try
                    {
                        foreach (var d in Directory.GetDirectories(cur))
                        {
                            if (IsJunctionPath(d)) continue;
                            stack.Push(d);
                        }
                    }
                    catch { }
                }
                return best;
            }
            catch { }
            return null;
        }

        /// <summary>相对库根的路径版本（FixPszPaths 内部用）。</summary>
        private static string FindRealRelPath(string libRoot, string cojtName, Action<string> log)
        {
            string abs = FindRealCojtInRoot(libRoot, cojtName, log);
            if (string.IsNullOrEmpty(abs)) return null;
            return abs.Substring(libRoot.TrimEnd('\\', '/').Length).TrimStart('\\', '/');
        }

        /// <summary>
        /// 在「本次实际插入的 cojt 路径」里找同名项，返回相对库根的路径。
        /// 同名 cojt 有多份时，优先用实际插入的那份，保证 psz 指向与插入一致。
        /// </summary>
        private static string FindKnownRealRel(string libRoot, string cojtName,
                                               IEnumerable<string> knownRealPaths)
        {
            try
            {
                if (string.IsNullOrEmpty(libRoot) || knownRealPaths == null) return null;
                string rootNorm = libRoot.TrimEnd('\\', '/');
                string best = null;
                foreach (var abs in knownRealPaths)
                {
                    if (string.IsNullOrWhiteSpace(abs)) continue;
                    string name = Path.GetFileName(abs.TrimEnd('\\', '/'));
                    if (!string.Equals(name, cojtName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!Directory.Exists(abs)) continue;
                    if (!abs.StartsWith(rootNorm + "\\", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(Path.GetPathRoot(abs), rootNorm + "\\", StringComparison.OrdinalIgnoreCase))
                        continue;
                    // 取相对路径
                    string rel;
                    if (abs.StartsWith(rootNorm + "\\", StringComparison.OrdinalIgnoreCase))
                        rel = abs.Substring(rootNorm.Length + 1);
                    else
                        rel = abs.Substring(rootNorm.Length).TrimStart('\\', '/');
                    if (string.IsNullOrEmpty(rel)) continue;
                    // 同名多份时选最深（真实库通常嵌套深）
                    if (best == null || DepthOf(rel) > DepthOf(best))
                        best = rel;
                }
                return best;
            }
            catch { return null; }
        }

        /// <summary>路径分隔符层数（/ 与 \ 都算）。</summary>
        private static int DepthOf(string relPath)
        {
            if (string.IsNullOrEmpty(relPath)) return 0;
            int n = 0;
            foreach (var c in relPath)
                if (c == '\\' || c == '/') n++;
            return n;
        }

        /// <summary>
        /// 修复 &lt;fileName&gt; 元素中的非法 XML 字符。
        /// 根因（2026-08-18 实测）：Sensor&amp;Light 中的 &amp; 在 XML 里是非法 token，
        /// PS 用严格解析器，一个良构错误就导致整包无法打开。
        /// 原版 psz 同一路径写作 Sensor&amp;amp;Light，所以它能打开。
        ///
        /// 修复范围（PS XML 解析器严格模式下均为非法 token）：
        ///   &amp;  → &amp;amp;     最常见（如 Sensor&amp;Light）
        ///   &lt;  → &amp;lt;       尖括号（路径极少见但破坏性极强，会把 &lt;fileName&gt; 后续 XML 元素截断）
        /// </summary>
        private static string EscapeXmlInFileNameElements(string content, Action<string> log)
        {
            int fixedCount = 0;
            int idx = 0;
            while (true)
            {
                int s = content.IndexOf("<fileName>", idx);
                if (s < 0) break;
                int e = content.IndexOf("</fileName>", s);
                if (e < 0) break;
                idx = e + 11;

                // 提取内容
                string before = content.Substring(0, s + 10);
                string inner = content.Substring(s + 10, e - s - 10);
                string after = content.Substring(e);

                // 先解码已有的 XML 实体，再统一转义非法字符（避免重复转义）
                string decoded = inner
                    .Replace("&amp;", "&").Replace("&lt;", "<")
                    .Replace("&gt;", ">").Replace("&apos;", "'").Replace("&quot;", "\"");
                string escaped = decoded
                    .Replace("&", "&amp;").Replace("<", "&lt;");

                if (!string.Equals(escaped, inner, StringComparison.Ordinal))
                {
                    content = before + escaped + after;
                    fixedCount++;
                    idx = s + 10 + escaped.Length + 11;
                }
            }
            if (fixedCount > 0)
                log("[Psz] ✓ XML 良构修复：转义 " + fixedCount + " 处非法字符（&/lt 等）");
            return content;
        }
    }
}
