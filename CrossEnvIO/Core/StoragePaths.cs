using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TxTools.CrossEnvIO
{
    internal static class StoragePaths
    {
        // SDK storage paths may remain relative to the temporary ASCII root after insertion.
        internal static string Normalize(string value, string root, IEnumerable<string> knownPaths, string linkDirectory)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            string path = value.Replace('/', '\\');
            bool rootToken = path.StartsWith("#\\",StringComparison.Ordinal);
            bool relative = rootToken || !Path.IsPathRooted(path);
            if (relative)
            {
                if (string.IsNullOrWhiteSpace(root)) throw new IOException("相对组件路径缺少库根: " + value);
                path = Path.Combine(root,rootToken?path.Substring(2):path);
                if (CojtTransfer.RelativeToRoot(path,root)==null) throw new IOException("组件路径越过库根: " + value);
            }
            string full = Path.GetFullPath(path).TrimEnd('\\','/');
            if (Directory.Exists(full)) return DirectoryLinks.Resolve(full);
            string name = Path.GetFileName(full);
            if (!IsAliasName(name)) return full;
            // Only generated aliases at the root or in the owned link directory can be translated.
            string parent = Path.GetDirectoryName(full);
            bool atRoot = Same(parent,root), atLink = Same(parent,linkDirectory);
            if (!atRoot && !atLink) return full;
            var matches = (knownPaths ?? Enumerable.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p)
                && Directory.Exists(p) && string.Equals(CojtTransfer.LinkNameForPath(p),name,StringComparison.OrdinalIgnoreCase))
                .Select(DirectoryLinks.Resolve).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (matches.Count==1 && (!atRoot || CojtTransfer.RelativeToRoot(matches[0],root)!=null)) return matches[0];
            if (!string.IsNullOrEmpty(linkDirectory))
            {
                string link = Path.Combine(linkDirectory,name);
                if (Directory.Exists(link) && (File.GetAttributes(link)&FileAttributes.ReparsePoint)!=0)
                {
                    string target = DirectoryLinks.Resolve(link);
                    if (string.Equals(CojtTransfer.LinkNameForPath(target),name,StringComparison.OrdinalIgnoreCase)
                        && (!atRoot || CojtTransfer.RelativeToRoot(target,root)!=null)) return target;
                }
            }
            return full;
        }
        internal static bool IsAliasName(string name) => name!=null && name.Length==74
            && name.StartsWith("Link_",StringComparison.OrdinalIgnoreCase) && name.EndsWith(".cojt",StringComparison.OrdinalIgnoreCase)
            && name.Substring(5,64).All(Uri.IsHexDigit);
        private static bool Same(string first,string second) => !string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second)
            && string.Equals(Path.GetFullPath(first).TrimEnd('\\','/'),Path.GetFullPath(second).TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase);
    }
}
