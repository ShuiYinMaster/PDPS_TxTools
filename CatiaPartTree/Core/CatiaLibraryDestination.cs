using System;
using System.Collections.Generic;
using System.IO;

namespace TxTools.CatiaPartTree.Core
{
    internal static class CatiaLibraryDestination
    {
        // The library folder is named by the customer ("product", "1_Products", ...), so
        // the default destination uses a fuzzy match instead of an exact "product" name.
        internal static string DefaultFolder(string library)
        {
            string fallback = Path.Combine(library, "product");
            if (!Directory.Exists(library)) return fallback;
            try
            {
                string exact = null, fuzzy = null;
                foreach (string path in Directory.EnumerateDirectories(library))
                {
                    string name = Path.GetFileName(path);
                    if (string.IsNullOrEmpty(name)) continue;
                    if (string.Equals(name, "product", StringComparison.OrdinalIgnoreCase)) { exact = name; break; }
                    if (name.IndexOf("product", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (fuzzy == null || name.Length < fuzzy.Length
                        || (name.Length == fuzzy.Length && string.Compare(name, fuzzy, StringComparison.OrdinalIgnoreCase) < 0))
                        fuzzy = name;
                }
                return Path.Combine(library, exact ?? fuzzy ?? "product");
            }
            catch (Exception) { return fallback; }
        }

        internal static string ResolveFolder(string library, string folder)
        {
            string path = string.IsNullOrWhiteSpace(folder) ? DefaultFolder(library) : folder.Trim();
            return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(library, path));
        }

        internal static string DocumentFolder(string folder, string document)
        {
            string name = Path.GetFileNameWithoutExtension(document ?? "");
            return string.IsNullOrWhiteSpace(name) ? folder : Path.Combine(folder, name);
        }

        internal static string FolderSegment(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "container";
            char[] invalid = Path.GetInvalidFileNameChars();
            var buffer = new char[name.Length];
            for (int i = 0; i < name.Length; i++)
                buffer[i] = Array.IndexOf(invalid, name[i]) >= 0 ? '_' : name[i];
            string cleaned = new string(buffer).Trim().TrimEnd('.', ' ');
            return string.IsNullOrEmpty(cleaned) ? "container" : cleaned;
        }

        internal static string Package(string destination, string source, ISet<string> reserved)
        {
            return Reserve(destination, Path.GetFileNameWithoutExtension(source), ".cojt", reserved);
        }

        internal static string Reserve(string destination, string name, string extension, ISet<string> reserved)
        {
            string path = Path.Combine(destination, name + extension);
            // Keep the package directly in the destination folder; same-name
            // sources get a numbered suffix instead of a nested bucket folder.
            for (int version = 2; Occupied(path, reserved) || (extension == ".jt" && File.Exists(path + ".pending")); version++)
                path = Path.Combine(destination, name + "_" + version + extension);
            reserved.Add(path);
            return path;
        }

        private static bool Occupied(string path, ISet<string> reserved)
        { return reserved.Contains(path) || Directory.Exists(path) || File.Exists(path); }

        internal static void CreatePackage(string path)
        {
            string parent = Path.GetDirectoryName(path);
            Directory.CreateDirectory(parent);
            string reservation = Path.Combine(parent, ".catia-" + Guid.NewGuid().ToString("N") + ".tmp");
            Directory.CreateDirectory(reservation);
            try { Directory.Move(reservation, path); }
            finally { if (Directory.Exists(reservation)) Directory.Delete(reservation); }
        }
    }
}
