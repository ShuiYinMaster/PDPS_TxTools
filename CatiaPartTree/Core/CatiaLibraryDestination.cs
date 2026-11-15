using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace TxTools.CatiaPartTree.Core
{
    internal static class CatiaLibraryDestination
    {
        internal static string Default(string library, string document)
        {
            string product = Directory.Exists(library) ? Directory.EnumerateDirectories(library)
                .FirstOrDefault(p => string.Equals(Path.GetFileName(p), "product", StringComparison.OrdinalIgnoreCase)) : null;
            return Path.Combine(product ?? Path.Combine(library, "product"), Path.GetFileNameWithoutExtension(document));
        }

        internal static string Resolve(string library, string destination, string document)
        {
            string path = string.IsNullOrWhiteSpace(destination) ? Default(library, document) : destination.Trim();
            return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(library, path));
        }

        internal static string Package(string destination, string source, ISet<string> reserved)
        {
            string name = Path.GetFileNameWithoutExtension(source) + ".cojt";
            string path = Path.Combine(destination, name);
            if (Occupied(path, reserved))
            {
                string parent = Path.GetFileName(Path.GetDirectoryName(source)) ?? "source";
                string identity;
                using (var sha = SHA256.Create())
                    identity = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(source).ToUpperInvariant()))).Replace("-", "").Substring(0, 8);
                string bucket = parent + "-" + identity;
                path = Path.Combine(destination, bucket, name);
                for (int version = 2; Occupied(path, reserved); version++)
                    path = Path.Combine(destination, bucket + "-" + version, name);
            }
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
