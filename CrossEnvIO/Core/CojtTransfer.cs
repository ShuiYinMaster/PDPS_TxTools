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
        public static bool CopyCojtDirectory(string srcDir, string dstParentDir, Action<string> log, CopyResult record = null)
        {
            log = log ?? Nop;
            string ownedPath = null;
            try
            {
                if (string.IsNullOrWhiteSpace(srcDir) || !Directory.Exists(srcDir))
                    throw new IOException("源 cojt 目录不存在: " + srcDir);
                if (string.IsNullOrWhiteSpace(dstParentDir)) throw new IOException("目标父目录为空");
                srcDir = Path.GetFullPath(srcDir).TrimEnd('\\', '/');
                dstParentDir = Path.GetFullPath(dstParentDir);
                Directory.CreateDirectory(dstParentDir);
                string dstDir = Path.Combine(dstParentDir, Path.GetFileName(srcDir));
                if (Directory.Exists(dstDir) || File.Exists(dstDir))
                    throw new IOException("目标已存在，未覆盖: " + dstDir);
                // 用唯一暂存目录复制，Move 发布不会覆盖已有目标。
                ownedPath = Path.Combine(dstParentDir, ".txtools-copy-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(ownedPath);
                if (record != null) record.Track(ownedPath);
                var pending = new Stack<string>(); pending.Push(srcDir);
                int count = 0;
                while (pending.Count > 0)
                {
                    string current = pending.Pop();
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("不复制链接目录: " + current);
                    string relative = current.Substring(srcDir.Length).TrimStart('\\', '/');
                    string destination = Path.Combine(ownedPath, relative);
                    Directory.CreateDirectory(destination);
                    foreach (string file in Directory.GetFiles(current))
                    {
                        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                            throw new IOException("不复制链接文件: " + file);
                        File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), false);
                        count++;
                    }
                    foreach (string child in Directory.GetDirectories(current)) pending.Push(child);
                }
                Directory.Move(ownedPath, dstDir);
                if (record != null) record.RenameTracked(ownedPath, dstDir);
                ownedPath = dstDir;
                log("[复制] ✓ " + srcDir + " → " + dstDir + " (" + count + " 个文件)");
                return true;
            }
            catch (Exception ex)
            {
                log("[复制] 异常: " + ex.Message);
                return false;
            }
            finally
            {
                // 包括失败复制的半成品。无法读取清单时保留记录、禁止自动删除。
                if (record != null && ownedPath != null) record.Capture(ownedPath, log);
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

                if (CopyCojtDirectory(src, dstParent, log, rep))
                {
                    rep.Ok++;
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
        public bool SceneMayReferenceFiles;
        public readonly List<string> CopiedDirs = new List<string>();
        private readonly Dictionary<string, Dictionary<string, string>> _snapshots =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        public override string ToString() => $"复制成功 {Ok} · 失败 {Fail} · 已跳过 {Skipped}";

        internal void Track(string directory)
        {
            if (!CopiedDirs.Contains(directory)) CopiedDirs.Add(directory);
        }
        internal void RenameTracked(string oldPath, string newPath)
        {
            CopiedDirs.Remove(oldPath); _snapshots.Remove(oldPath); Track(newPath);
        }
        internal void Capture(string directory, Action<string> log)
        {
            try { _snapshots[directory] = Snapshot(directory); }
            catch (Exception ex) { log("[复制] 无法记录文件清单，保留目录且不允许自动清理: " + directory + " - " + ex.Message); }
        }
        /// <summary>保留之前批次的清理记录和模型依赖保护，避免新复制覆盖旧记录。</summary>
        public void Merge(CopyResult previous)
        {
            if (previous == null) return;
            foreach (var directory in previous.CopiedDirs) Track(directory);
            foreach (var item in previous._snapshots) _snapshots[item.Key] = item.Value;
            SceneMayReferenceFiles |= previous.SceneMayReferenceFiles;
        }

        private static Dictionary<string, string> Snapshot(string directory)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>(); pending.Push(directory);
            while (pending.Count > 0)
            {
                string current = pending.Pop();
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("目录已变成链接");
                result["D:" + current.Substring(directory.Length)] = "directory";
                foreach (string file in Directory.GetFiles(current))
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("文件已变成链接");
                    using (var sha = System.Security.Cryptography.SHA256.Create())
                    using (var stream = File.OpenRead(file))
                        result["F:" + file.Substring(directory.Length)] = Convert.ToBase64String(sha.ComputeHash(stream));
                }
                foreach (string child in Directory.GetDirectories(current)) pending.Push(child);
            }
            return result;
        }

        /// <summary>文件清理独立于场景撤销；调用方必须先检查当前工程引用。</summary>
        public void Undo(Action<string> log, Func<string, bool> canRemove = null)
        {
            log = log ?? delegate { };
            if (SceneMayReferenceFiles)
            { log("[清理] 已进行场景重建，保留模型依赖和记录。请关闭相关工程后手动核对库文件。"); return; }
            if (canRemove == null)
            { log("[清理] 缺少工程引用检查，已保留全部文件和记录。"); return; }
            int ok = 0, fail = 0;
            foreach (string directory in new List<string>(CopiedDirs))
            {
                try
                {
                    if (!Directory.Exists(directory))
                    { CopiedDirs.Remove(directory); _snapshots.Remove(directory); continue; }
                    Dictionary<string, string> before;
                    if (!_snapshots.TryGetValue(directory, out before))
                        throw new IOException("没有所有权清单，不能自动清理");
                    if (!canRemove(directory)) throw new IOException("工程仍引用此目录或无法核验引用");
                    var now = Snapshot(directory);
                    if (before.Count != now.Count) throw new IOException("复制后目录内容发生变化");
                    foreach (var entry in before)
                    {
                        string value;
                        if (!now.TryGetValue(entry.Key, out value) || value != entry.Value)
                            throw new IOException("复制后文件内容发生变化");
                    }
                    Directory.Delete(directory, true);
                    CopiedDirs.Remove(directory); _snapshots.Remove(directory); ok++;
                }
                catch (Exception ex)
                { fail++; log("[清理] 未删除，保留记录以便重试: " + directory + " - " + ex.Message); }
            }
            log("[清理] 完成: 删除 " + ok + " 个，保留 " + fail + " 个；不等同于 PS Ctrl+Z。");
        }
    }
}
