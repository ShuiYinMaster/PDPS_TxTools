using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TxTools.ExportGun
{
    internal static class CgrOutputPaths
    {
        // CgrWriter appends "." + 32 hex digits + ".partial" (41 chars).
        // Keep both final and temporary paths below legacy .NET/CATIA limits.
        internal const int MaxOutputLength = 200;

        internal static string Create(string preferredDirectory, string toolName, IEnumerable<string> cacheRoots = null)
        {
            string original = string.IsNullOrWhiteSpace(toolName) ? "Gun" : toolName.Trim();
            string safe = original;
            foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
            safe = safe.TrimEnd(' ', '.');
            if (safe.Length == 0) safe = "Gun";
            string device = safe.Split('.')[0].ToUpperInvariant();
            if (device == "CON" || device == "PRN" || device == "AUX" || device == "NUL"
                || (device.Length == 4 && (device.StartsWith("COM") || device.StartsWith("LPT"))
                    && device[3] >= '1' && device[3] <= '9')) safe = "_" + safe;
            string digest;
            using (var sha = SHA256.Create())
                digest = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(original))).Replace("-", "").Substring(0, 10);

            string directory = NormalizeShortDirectory(preferredDirectory);
            if (directory != null && Directory.Exists(directory))
            {
                string path = AvailablePath(directory, safe, digest);
                if (path != null) return path;
            }

            if (cacheRoots == null) cacheRoots = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TxTools", "Cgr"),
                Path.Combine(Path.GetTempPath(), "TxCgr"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".txcgr")
            };
            Exception lastError = null;
            foreach (string root in cacheRoots)
            {
                string shortRoot = NormalizeShortDirectory(root);
                if (shortRoot == null) continue;
                string cacheDirectory = Path.Combine(shortRoot, Guid.NewGuid().ToString("N").Substring(0, 12));
                string path = AvailablePath(cacheDirectory, safe, digest);
                if (path == null) continue;
                try
                {
                    Directory.CreateDirectory(cacheDirectory);
                    return path; // Retained: CATIA may keep this CGR as an external reference.
                }
                catch (IOException ex) { lastError = ex; }
                catch (UnauthorizedAccessException ex) { lastError = ex; }
            }
            throw new IOException("未找到可写且足够短的 CGR 输出目录，请检查本机缓存目录。", lastError);
        }

        private static string NormalizeShortDirectory(string directory)
        {
            // Reject long raw paths before GetFullPath can throw PathTooLongException.
            if (string.IsNullOrWhiteSpace(directory) || directory.Length > MaxOutputLength - 21) return null;
            try
            {
                string full = Path.GetFullPath(directory);
                return full.Length <= MaxOutputLength - 21 ? full : null;
            }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (PathTooLongException) { return null; }
        }

        private static string AvailablePath(string directory, string name, string digest)
        {
            int budget = MaxOutputLength - directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length - 1 - 4;
            if (budget < 16) return null;
            for (int index = 0; index < 10000; index++)
            {
                string suffix = index == 0 ? "" : "-" + index.ToString(CultureInfo.InvariantCulture);
                int limit = budget - suffix.Length;
                string stem = name.Length <= limit ? name : name.Substring(0, limit - 11).TrimEnd(' ', '.') + "-" + digest;
                string path = Path.Combine(directory, stem + suffix + ".cgr");
                if (!File.Exists(path) && !Directory.Exists(path)) return path;
            }
            return null;
        }
    }
}
