using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace TxTools.CrossEnvIO
{
    public sealed class PackageInspection
    {
        public string Hash;
        public long Bytes;
        public List<string> ExternalReferences = new List<string>();
        public List<string> AbsoluteReferences = new List<string>();
    }

    internal static class PackageFiles
    {
        public static string HashBytes(byte[] bytes)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", ""); }
        public static string HashFile(string file)
        {
            using (var sha = SHA256.Create())
            using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "");
        }

        public static PackageInspection Inspect(string root)
        {
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("组件目录不存在: " + root);
            root = Path.GetFullPath(root).TrimEnd('\\', '/');
            TransferPaths.CheckParents(root);
            var entries = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var result = new PackageInspection();
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.Count > 0)
            {
                string current = pending.Pop();
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("组件内部含链接: " + current);
                entries["D:" + current.Substring(root.Length)] = "directory";
                foreach (string child in Directory.GetDirectories(current)) pending.Push(child);
                foreach (string file in Directory.GetFiles(current))
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("组件内部含链接文件: " + file);
                    entries["F:" + file.Substring(root.Length)] = HashFile(file);
                    result.Bytes = checked(result.Bytes + new FileInfo(file).Length);
                    if (Path.GetExtension(file).Equals(".xml", StringComparison.OrdinalIgnoreCase))
                        ReadReferences(file, root, result.ExternalReferences, result.AbsoluteReferences);
                }
            }
            result.Hash = HashBytes(Encoding.UTF8.GetBytes(string.Join("\n", entries.Select(e => e.Key + "\t" + e.Value))));
            result.ExternalReferences = result.ExternalReferences.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return result;
        }

        private static bool PathField(string name)
        {
            name = name.ToLowerInvariant();
            return name.Contains("filename") || name.EndsWith("path") || name == "file" || name == "href" || name == "src";
        }

        private static void ReadReferences(string file, string root, List<string> issues, List<string> absolute)
        {
            try
            {
                using (var reader = XmlReader.Create(file, new XmlReaderSettings
                { XmlResolver = null, DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 32L * 1024 * 1024 }))
                {
                    string field = null;
                    while (reader.Read())
                    {
                        if (reader.NodeType == XmlNodeType.Element)
                        {
                            field = PathField(reader.LocalName) ? reader.LocalName : null;
                            if (reader.HasAttributes)
                            {
                                while (reader.MoveToNextAttribute())
                                    if (PathField(reader.LocalName)) CheckReference(reader.Value, file, root, issues, absolute);
                                reader.MoveToElement();
                            }
                        }
                        else if ((reader.NodeType == XmlNodeType.Text || reader.NodeType == XmlNodeType.CDATA) && field != null)
                            CheckReference(reader.Value, file, root, issues, absolute);
                        else if (reader.NodeType == XmlNodeType.EndElement) field = null;
                    }
                }
            }
            catch (Exception ex) { issues.Add("XML 依赖无法核验: " + Path.GetFileName(file) + " - " + ex.Message); }
        }

        private static void CheckReference(string value, string xmlFile, string package, List<string> issues, List<string> absolute)
        {
            value = (value ?? "").Trim();
            if (value.Length == 0) return;
            string ext;
            try { ext = Path.GetExtension(value).ToLowerInvariant(); }
            catch { issues.Add(value); return; }
            if (!new[] { ".jt", ".cgr", ".cojt", ".catpart", ".catproduct", ".stl", ".obj", ".png", ".jpg", ".jpeg", ".bmp" }.Contains(ext)) return;
            if (value.IndexOf('#') >= 0 || value.IndexOf('$') >= 0 || value.Contains("://")) { issues.Add(value); return; }
            string full = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(Path.GetDirectoryName(xmlFile), value));
            if (!TransferPaths.Same(full, package) && CojtTransfer.RelativeToRoot(full, package) == null)
                issues.Add(value + "（包外引用，需要重新包装或绑定）");
            else if (!File.Exists(full) && !Directory.Exists(full)) issues.Add(value + "（源文件缺失）");
            else if (Path.IsPathRooted(value)) absolute.Add(value);
        }
    }
}
