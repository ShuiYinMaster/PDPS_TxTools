using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace TxTools.FootingPlanExport
{
    // No SDK calls: file ownership, atomic final save and success-only cleanup.
    internal sealed class CacheSession
    {
        internal const string CacheRelativeRoot = "TxToolsCache\\FootingPlanExport";
        internal string Root { get; }
        // 缓存根：%TEMP%\TxToolsCache\FootingPlanExport，本次运行目录是它的子目录。
        internal string CacheRoot => Path.Combine(Root, CacheRelativeRoot);
        internal string RunId { get; }
        internal string RunDirectory { get; }
        internal string PlannedCojtPath => Path.Combine(RunDirectory, "FootingPlan_" + RunId + ".cojt");
        // 另存为对话框只能填纯文件名，这里给出本次唯一的 cojt 文件名；
        // 实际落盘目录取自对话框本身，可能是缓存目录，也可能是 PS 记住的上次目录。
        internal string SectionCojtPath => Path.Combine(RunDirectory, "Contour_" + RunId + ".cojt");
        internal static CacheSession CreateTemporary() => new CacheSession(Path.GetTempPath());
        internal string DxfPath => Path.Combine(RunDirectory, "FootingPlan.dxf");
        internal string CojtPath { get; private set; }
        internal string SavedPath { get; private set; }
        internal bool SceneDetached { get; set; }
        private string MarkerPath => Path.Combine(RunDirectory, ".footing-export-owner");
        private string _savedHash;

        internal CacheSession(string cacheRoot)
        {
            if (string.IsNullOrWhiteSpace(cacheRoot) || !Directory.Exists(cacheRoot))
                throw new IOException("系统临时目录无效，无法创建 COJT 缓存。");
            Root = Path.GetFullPath(cacheRoot);
            RunId = Guid.NewGuid().ToString("N");
            RunDirectory = Path.GetFullPath(Path.Combine(Root, CacheRelativeRoot, RunId));
            CheckContained(RunDirectory, Root);
            var ancestor = RunDirectory;
            while (!Directory.Exists(ancestor)) ancestor = Path.GetDirectoryName(ancestor);
            CheckNoLinks(ancestor, Root);
            Directory.CreateDirectory(RunDirectory);
            CheckNoLinks(RunDirectory, Root);
            File.WriteAllText(MarkerPath, RunId, Encoding.ASCII);
        }

        internal void SetCojtPath(string path)
        {
            var full = Path.GetFullPath(path);
            CheckContained(full, RunDirectory);
            if (!full.EndsWith(".cojt", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(full))
                throw new IOException("SDK 没有生成有效的 COJT 缓存目录：" + full);
            CheckNoLinks(full, RunDirectory);
            CojtPath = full;
        }

        // A visualization-only .cojt staging directory, not a library resource.
        // The JT bytes are serialized by PS's native contour component itself.
        internal string StageLocalJt(string source)
        {
            VerifyOwner();
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source)) throw new IOException("SDK 未返回有效的本地截面 JT 文件：" + source);
            CheckContained(PlannedCojtPath, RunDirectory);
            Directory.CreateDirectory(PlannedCojtPath);
            CheckNoLinks(PlannedCojtPath, Root);
            string target = Path.Combine(PlannedCojtPath, "FootingPlan.jt");
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var header = new byte[80];
                if (input.Read(header, 0, 80) != 80 || !Encoding.ASCII.GetString(header).StartsWith("Version ", StringComparison.Ordinal))
                    throw new IOException("SDK 本地保存结果不是有效的 JT 文件。");
                input.Position = 0;
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
                if (new FileInfo(target).Length != input.Length) throw new IOException("截面 JT 缓存复制不完整。");
            }
            SetCojtPath(PlannedCojtPath);
            return FindJt();
        }

        internal string StageStoredJt(string path)
        {
            if (File.Exists(path)) return StageLocalJt(path);
            if (!Directory.Exists(path)) throw new IOException("原生截面组件存储不存在：" + path);
            string jt = FindJtFile(path);
            if (jt == null) throw new IOException("截面 cojt 里没有找到 JT 文件：" + path);
            return StageLocalJt(jt);
        }

        // 另存为对话框的目录读不到时的兜底：按本次唯一的 cojt 名在有限的常见
        // 根目录里做深度受限的搜索。名称带 GUID，命中即本次产物。
        internal static string FindCojtByName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;
            var roots = new List<Tuple<string, int>>();
            AddRoot(roots, Path.GetTempPath(), 3);
            AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), 3);
            AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), 2);
            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                    if (drive.DriveType == DriveType.Fixed || drive.DriveType == DriveType.Removable)
                        AddRoot(roots, drive.RootDirectory.FullName, 2);
            }
            catch (IOException) { }
            int budget = 30000;
            foreach (var root in roots)
            {
                string hit = Probe(root.Item1, fileName, 0, root.Item2, ref budget);
                if (hit != null) return hit;
            }
            return null;
        }

        private static void AddRoot(List<Tuple<string, int>> roots, string path, int depth)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
            roots.Add(Tuple.Create(path.TrimEnd('\\', '/'), depth));
        }

        private static string Probe(string directory, string fileName, int level, int maxDepth, ref int budget)
        {
            if (budget-- <= 0) return null;
            try
            {
                string candidate = Path.Combine(directory, fileName);
                if (Directory.Exists(candidate)) return candidate;
                if (level >= maxDepth) return null;
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    string hit = Probe(child, fileName, level + 1, maxDepth, ref budget);
                    if (hit != null) return hit;
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            return null;
        }

        // PS 直接另存出来的 cojt 目录层级不固定，递归取最浅层的 JT。
        internal static string FindJtFile(string directory)
        {
            try
            {
                if (!Directory.Exists(directory)) return null;
                string best = null;
                int bestDepth = int.MaxValue;
                foreach (var file in Directory.GetFiles(directory, "*.jt", SearchOption.AllDirectories))
                {
                    int depth = file.Length - directory.Length;
                    if (depth < bestDepth) { best = file; bestDepth = depth; }
                }
                return best;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        // PS 的另存为不会采用我们指定的文件名和目录，实际产物可能是缓存根下
        // 一个以 GUID 命名的 cojt。因此在命令执行前后对缓存根做快照，只认本次
        // 新出现的那一个，避免依赖文件名或对话框读目录。
        internal HashSet<string> SnapshotCojts()
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var directory in Directory.GetDirectories(CacheRoot, "*.cojt", SearchOption.TopDirectoryOnly))
                    result.Add(directory);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            try
            {
                foreach (var directory in Directory.GetDirectories(RunDirectory, "*.cojt", SearchOption.TopDirectoryOnly))
                    result.Add(directory);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return result;
        }

        internal string FindNewCojt(HashSet<string> before)
        {
            foreach (var directory in SnapshotCojts())
            {
                if (before != null && before.Contains(directory)) continue;
                if (string.Equals(directory, PlannedCojtPath, StringComparison.OrdinalIgnoreCase)) continue;
                if (!IsCojtReady(directory)) continue;
                return directory;
            }
            return null;
        }

        // 原生命令写 cojt 是先建目录后写文件，必须等到顶层 JT 真正落盘。
        internal bool TryGetSectionCojt(out string directory)
        {
            directory = null;
            try
            {
                if (IsCojtReady(SectionCojtPath)) { directory = SectionCojtPath; return true; }
                foreach (var candidate in Directory.GetDirectories(RunDirectory, "*.cojt", SearchOption.TopDirectoryOnly))
                {
                    if (string.Equals(candidate, PlannedCojtPath, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!IsCojtReady(candidate)) continue;
                    directory = candidate;
                    return true;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return false;
        }

        internal static bool IsCojtReady(string directory) => FindJtFile(directory) != null;

        internal string FindJt()
        {
            if (CojtPath == null) throw new IOException("尚未生成 COJT。");
            CheckNoLinks(CojtPath, RunDirectory);
            var files = Directory.GetFiles(CojtPath, "*", SearchOption.TopDirectoryOnly)
                .Where(p => p.EndsWith(".jt", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (files.Length != 1) throw new IOException("COJT 需要唯一的顶层 JT 文件，实际为 " + files.Length + " 个。");
            CheckNoLinks(files[0], RunDirectory);
            return files[0];
        }

        internal void SaveDxf(string destination)
        {
            VerifyOwner();
            var full = Path.GetFullPath(destination);
            if (!full.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase)) throw new IOException("保存文件必须使用 .dxf 扩展名。");
            var cacheRoot = Path.Combine(Root, CacheRelativeRoot);
            if (IsContained(full, cacheRoot) || string.Equals(full, DxfPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("请选择缓存目录以外的 DXF 保存位置。");
            if (!File.Exists(DxfPath) || new FileInfo(DxfPath).Length == 0) throw new IOException("转换后的 DXF 不存在或为空。");
            var parent = Path.GetDirectoryName(full);
            if (!Directory.Exists(parent)) throw new IOException("保存目录不存在。");
            if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("不能覆盖符号链接文件。");
            var pending = Path.Combine(parent, ".footing-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.Copy(DxfPath, pending, false);
                var hash = Hash(DxfPath);
                if (hash != Hash(pending)) throw new IOException("DXF 复制校验失败。");
                if (File.Exists(full)) File.Replace(pending, full, null); else File.Move(pending, full);
                if (hash != Hash(full)) throw new IOException("DXF 保存后校验失败，已保留缓存。");
                SavedPath = full;
                _savedHash = hash;
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
        }

        internal void CleanupAfterSave()
        {
            VerifyOwner();
            if (!SceneDetached) throw new IOException("临时截面仍可能引用 COJT，缓存已保留。");
            if (SavedPath == null || !File.Exists(SavedPath) || Hash(SavedPath) != _savedHash)
                throw new IOException("尚未成功保存 DXF，缓存已保留。");
            CheckContained(RunDirectory, Path.Combine(Root, CacheRelativeRoot));
            // Validate every descendant before issuing recursive deletion.
            var pending = new System.Collections.Generic.Stack<string>();
            pending.Push(RunDirectory);
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                CheckNoLinks(directory, Root);
                foreach (var file in Directory.GetFiles(directory)) CheckNoLinks(file, Root);
                foreach (var child in Directory.GetDirectories(directory)) pending.Push(child);
            }
            Directory.Delete(RunDirectory, true);
        }

        private void VerifyOwner()
        {
            CheckContained(RunDirectory, Path.Combine(Root, CacheRelativeRoot));
            CheckNoLinks(RunDirectory, Root);
            CheckNoLinks(MarkerPath, Root);
            if (!File.Exists(MarkerPath) || File.ReadAllText(MarkerPath, Encoding.ASCII) != RunId)
                throw new IOException("缓存所有权校验失败。");
        }

        private static string Hash(string file)
        {
            using (var sha = SHA256.Create()) using (var stream = File.OpenRead(file))
                return BitConverter.ToString(sha.ComputeHash(stream));
        }
        private static bool IsContained(string path, string root) => Path.GetFullPath(path).StartsWith(
            Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        private static void CheckContained(string path, string root)
        { if (!IsContained(path, root)) throw new IOException("缓存路径越界：" + path); }
        private static void CheckNoLinks(string path, string root)
        {
            var current = Path.GetFullPath(path);
            var stop = Path.GetFullPath(root).TrimEnd('\\', '/');
            if (!string.Equals(current.TrimEnd('\\', '/'), stop, StringComparison.OrdinalIgnoreCase) && !IsContained(current, root))
                throw new IOException("缓存路径越界：" + current);
            while (true)
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("缓存路径不能包含链接：" + current);
                if (string.Equals(current.TrimEnd('\\', '/'), stop, StringComparison.OrdinalIgnoreCase)) break;
                current = Path.GetDirectoryName(current);
                if (current == null) throw new IOException("无法校验缓存路径。");
            }
        }
    }
}
