// CojtTransfer.cs  --  C# 8.0
// 跨环境 cojt 目录传输工具。
//
// 背景：.cojt 是「目录」不是文件（内含 .jt/.cgr/kin_graph.xml/TuneData.xml 等），
//       Directory.Copy 在 PS 沙箱内不可用，必须用栈式 File.Copy 逐文件复制。
//       复制后 cojt 位于目标库 SystemRootDirectory 范围内，库浏览器刷新即可见。

using System;
using System.Collections.Generic;
using System.IO;

namespace TxTools.CrossEnvIO
{
    public static class CojtTransfer
    {
        private static void Nop(string s) { }

        /// <summary>
        /// 把一个 .cojt 目录整体复制到目标父目录（保留目录名）。
        /// 栈式逐文件复制，避免 Directory.Copy 在 PS 沙箱内不可用的问题。
        /// </summary>
        public static bool CopyCojtDirectory(string srcDir, string dstParentDir, Action<string> log)
        {
            log = log ?? Nop;
            try
            {
                if (string.IsNullOrWhiteSpace(srcDir) || !Directory.Exists(srcDir))
                {
                    log("[复制] 源 cojt 目录不存在: " + srcDir);
                    return false;
                }
                if (string.IsNullOrWhiteSpace(dstParentDir)) { log("[复制] 目标父目录为空"); return false; }
                Directory.CreateDirectory(dstParentDir);

                string name = Path.GetFileName(srcDir.TrimEnd('\\', '/'));
                string dstDir = Path.Combine(dstParentDir, name);

                // 目录结构
                var dirs = new List<string> { srcDir };
                while (dirs.Count > 0)
                {
                    string d = dirs[dirs.Count - 1];
                    dirs.RemoveAt(dirs.Count - 1);
                    foreach (var sub in Directory.GetDirectories(d))
                        dirs.Add(sub);
                    Directory.CreateDirectory(d.Replace(srcDir, dstDir));
                }

                // 文件（栈式）
                var files = new List<string>();
                var stack = new Stack<string>();
                stack.Push(srcDir);
                while (stack.Count > 0)
                {
                    string cur = stack.Pop();
                    foreach (var f in Directory.GetFiles(cur))
                        files.Add(f);
                    foreach (var d in Directory.GetDirectories(cur))
                        stack.Push(d);
                }
                foreach (var f in files)
                {
                    string dst = f.Replace(srcDir, dstDir);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    File.Copy(f, dst, true);
                }

                log("[复制] ✓ " + srcDir + " → " + dstDir + " (" + files.Count + " 个文件)");
                return true;
            }
            catch (Exception ex)
            {
                log("[复制] 异常: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 扫描目录下所有 *.cojt 子目录（含子层），返回绝对路径列表（DFS，去重，不跟随符号链接）。
        /// 用于「整体复制某个库目录下所有 cojt」。
        /// </summary>
        public static List<string> CollectCojtPaths(string rootDir, Action<string> log)
        {
            log = log ?? Nop;
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir)) return result;

            var stack = new Stack<string>();
            stack.Push(rootDir);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (stack.Count > 0)
            {
                string cur = stack.Pop();
                if (!Directory.Exists(cur)) continue;
                try
                {
                    foreach (var d in Directory.GetDirectories(cur))
                    {
                        if (d.EndsWith(".cojt", StringComparison.OrdinalIgnoreCase))
                        {
                            if (seen.Add(d)) result.Add(d);
                        }
                        else
                        {
                            stack.Push(d);
                        }
                    }
                }
                catch { }
            }
            log("[扫描] 共发现 .cojt 目录 " + result.Count + " 个");
            return result;
        }

        /// <summary>
        /// 把一批 cojt 目录按相对路径映射复制到目标库根。srcRoot 与 dstRoot 为对应库目录。
        ///
        /// 目标路径的【中间目录层】做字母部分模糊匹配（忽略数字/符号，如 1、Product 与
        /// 1_Product 视为同一目录），复用目标库已有的同字母目录，避免命名变体产生一堆文件夹；
        /// 最后一层（cojt 目录名）精确匹配，保证每个资源唯一。
        /// </summary>
        public static CopyResult CopyCojtList(List<string> srcCojtDirs, string srcRoot,
                                              string dstRoot, Action<string> log)
        {
            log = log ?? Nop;
            var rep = new CopyResult();
            if (srcCojtDirs == null || srcCojtDirs.Count == 0) return rep;

            string srcRootNorm = (srcRoot ?? "").TrimEnd('\\', '/');
            string dstRootNorm = (dstRoot ?? "").TrimEnd('\\', '/');
            if (string.IsNullOrEmpty(dstRootNorm)) { log("[复制] 目标库根为空"); return rep; }
            Directory.CreateDirectory(dstRootNorm);

            foreach (var src in srcCojtDirs)
            {
                string rel = src;
                try
                {
                    if (!string.IsNullOrEmpty(srcRootNorm)
                        && src.StartsWith(srcRootNorm + "\\", StringComparison.OrdinalIgnoreCase))
                        rel = src.Substring(srcRootNorm.Length + 1);
                    else
                        rel = Path.GetFileName(src);
                }
                catch { rel = Path.GetFileName(src); }

                string dstParent = ResolveDstParent(rel, dstRootNorm, log);
                if (string.IsNullOrEmpty(dstParent))
                {
                    rep.Fail++;
                    log("[复制] ✗ 无法解析目标目录: " + rel);
                    continue;
                }

                // 对端已有同名资源（cojt 目录已存在）→ 跳过，不覆盖
                string cojtName = Path.GetFileName(src.TrimEnd('\\', '/'));
                string dstDir = Path.Combine(dstParent, cojtName);
                if (Directory.Exists(dstDir))
                {
                    rep.Skipped++;
                    log("[跳过] 目标已存在，跳过: " + cojtName);
                    continue;
                }

                if (CopyCojtDirectory(src, dstParent, log))
                {
                    rep.Ok++;
                    rep.CopiedDirs.Add(dstDir);  // 记录实际复制的目录，用于撤销
                }
                else rep.Fail++;
            }
            return rep;
        }

        /// <summary>
        /// 把相对路径（如 1、Product\VAN\F30-5400710\xxx.cojt）解析到目标库根下，
        /// 中间目录逐层用字母模糊匹配复用已有目录。
        /// </summary>
        private static string ResolveDstParent(string relPath, string dstRoot, Action<string> log)
        {
            try
            {
                var segs = relPath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (segs.Length == 0) return dstRoot;

                // 最后一层是 cojt 目录名，它的父才是目标父目录
                int midCount = segs.Length - 1;
                if (midCount <= 0) return dstRoot;

                string cur = dstRoot;
                for (int i = 0; i < midCount; i++)
                    cur = MatchDirByAlpha(cur, segs[i]);
                return cur;
            }
            catch (Exception ex)
            {
                log("[复制] ResolveDstParent 异常: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 在 parentDir 下匹配/创建目录：优先复用「字母部分相同」的已有子目录，
        /// 没有则新建。避免 1、Product / 1_Product / 1 等命名变体各建一个文件夹。
        /// </summary>
        private static string MatchDirByAlpha(string parentDir, string name)
        {
            string targetAlpha = AlphaPart(name);
            try
            {
                foreach (var d in Directory.GetDirectories(parentDir))
                {
                    string dn = Path.GetFileName(d);
                    // 精确名优先（同字母下优先完全一致）
                    if (string.Equals(dn, name, StringComparison.OrdinalIgnoreCase))
                        return d;
                }
                foreach (var d in Directory.GetDirectories(parentDir))
                {
                    string dn = Path.GetFileName(d);
                    if (AlphaPart(dn) == targetAlpha)
                        return d;
                }
            }
            catch { }
            string nd = Path.Combine(parentDir, name);
            Directory.CreateDirectory(nd);
            return nd;
        }

        /// <summary>提取字符串的字母部分（仅保留字母，统一小写），用于模糊匹配。</summary>
        private static string AlphaPart(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var c in s)
                if (char.IsLetter(c))
                    sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }
    }

    public sealed class CopyResult
    {
        public int Ok;
        public int Fail;
        public int Skipped;
        public readonly List<string> CopiedDirs = new List<string>();

        public override string ToString() => $"复制成功 {Ok} · 失败 {Fail} · 已跳过 {Skipped}";

        /// <summary>
        /// 撤销：删除本次实际复制的 cojt 目录。对端的 cojt 已被 PS 锁定时可能删除失败，
        /// 日志记录每个失败项，重启 PS 后可手动清理。
        /// </summary>
        public void Undo(Action<string> log)
        {
            log = log ?? (s => { });
            if (CopiedDirs.Count == 0) { log("[撤销] 无已复制的 cojt，无需撤销"); return; }
            int ok = 0, fail = 0;
            foreach (var dir in CopiedDirs)
            {
                try
                {
                    if (Directory.Exists(dir))
                    {
                        Directory.Delete(dir, true);
                        ok++;
                    }
                    else
                    {
                        log("[撤销] 已不存在: " + dir);
                        fail++;
                    }
                }
                catch (IOException)
                {
                    fail++;
                    log("[撤销] 目录被 PS 进程锁定，请关闭 PS 后手动删除: " + dir);
                }
                catch (Exception ex)
                {
                    fail++;
                    log("[撤销] 删除失败: " + dir + " - " + ex.Message);
                }
            }
            CopiedDirs.Clear();
            log("[撤销] 完成: 删除 " + ok + " 个，失败 " + fail + " 个");
        }
    }
}
