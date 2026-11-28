using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TxTools.ResourceImporter
{
    internal sealed class ResourceFolderMatch
    {
        internal string Path;
        internal string Relative;
        internal string Name;
        internal int Score;
    }

    // Directory lookup shared by the folder search box and the prototype/folder
    // linkage. English names are compared case/camel-case insensitively and the
    // common resource categories also match their Chinese aliases.
    internal static class ResourceFolderSearch
    {
        internal const int AutoLocateScore = 600;
        private const int MaxDirectories = 20000;
        private static readonly string[] Noise = { "pm", "prototype", "type", "class", "resource" };
        private static readonly (string Name, string[] Aliases)[] Categories =
        {
            ("Gun", new[] { "gun", "weldgun", "weldinggun", "焊枪", "焊钳", "点焊枪", "焊钳" }),
            ("Robot", new[] { "robot", "机器人", "机械手" }),
            ("Fixture", new[] { "fixture", "夹具", "工装", "定位夹具", "工装夹具" }),
            ("Gripper", new[] { "gripper", "抓手", "夹爪", "端拾器", "抓取器", "夹手" })
        };

        internal static List<ResourceFolderMatch> Search(string library, string query, int limit, out int total)
        {
            total = 0;
            var result = new List<ResourceFolderMatch>();
            string normalized = Normalize(query);
            if (string.IsNullOrEmpty(library) || normalized.Length == 0 || limit <= 0) return result;
            foreach (var directory in Directories(library))
            {
                int score = Score(normalized, directory.Name);
                if (score <= 0) continue;
                directory.Score = score;
                result.Add(directory);
                total++;
            }
            result.Sort((left, right) =>
            {
                int order = right.Score.CompareTo(left.Score);
                return order != 0 ? order : string.Compare(left.Relative, right.Relative, StringComparison.CurrentCultureIgnoreCase);
            });
            if (result.Count > limit) result.RemoveRange(limit, result.Count - limit);
            return result;
        }

        private static IEnumerable<ResourceFolderMatch> Directories(string library)
        {
            var pending = new Stack<string>();
            pending.Push(library);
            int visited = 0;
            while (pending.Count != 0 && visited < MaxDirectories)
            {
                string parent = pending.Pop();
                DirectoryInfo[] children;
                try { children = new DirectoryInfo(parent).GetDirectories(); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { continue; }
                foreach (var child in children)
                {
                    if (++visited > MaxDirectories) break;
                    try
                    {
                        if ((child.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { continue; }
                    if (Excluded(child.Name)) continue;
                    pending.Push(child.FullName);
                    yield return new ResourceFolderMatch
                    {
                        Path = child.FullName,
                        Name = child.Name,
                        Relative = Relative(library, child.FullName)
                    };
                }
            }
        }

        private static bool Excluded(string name) => name.EndsWith(".cojt", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(CojtFolderTree.StagePrefix, StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(".tx-resource-import-", StringComparison.OrdinalIgnoreCase);

        private static string Relative(string library, string path)
        {
            string root = library.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return path.Length > root.Length ? path.Substring(root.Length).TrimStart('\\', '/') : path;
        }

        private static int Score(string query, string name)
        {
            string normalized = Normalize(name);
            if (normalized.Length == 0) return 0;
            int score = 0;
            string queryCategory = Category(query), folderCategory = Category(normalized);
            if (queryCategory != null && queryCategory == folderCategory) score += 1200;
            if (normalized == query) score += 1000;
            else if (normalized.IndexOf(query, StringComparison.Ordinal) >= 0) score += 800 + Math.Min(200, query.Length * 10);
            else if (query.IndexOf(normalized, StringComparison.Ordinal) >= 0 && normalized.Length >= 3) score += 600;
            else if (normalized.StartsWith(query, StringComparison.Ordinal) || query.StartsWith(normalized, StringComparison.Ordinal))
                score += 200;
            var queryTokens = Tokens(query);
            var nameTokens = Tokens(normalized);
            int overlap = queryTokens.Count(token => token.Length >= 2 &&
                nameTokens.Any(other => other == token || other.StartsWith(token, StringComparison.Ordinal) ||
                    token.StartsWith(other, StringComparison.Ordinal)));
            score += overlap * 180;
            double similarity = 0;
            foreach (string first in queryTokens.Where(token => token.Length >= 2))
                foreach (string second in nameTokens.Where(token => token.Length >= 2))
                    similarity = Math.Max(similarity, Similarity(first, second));
            if (similarity >= 0.7) score += (int)Math.Round(similarity * 400);
            return score;
        }

        internal static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var text = new StringBuilder(value.Length + 8);
            bool previousLower = false;
            foreach (char raw in value)
            {
                if (char.IsLetterOrDigit(raw))
                {
                    if (char.IsUpper(raw) && previousLower && text.Length != 0 && text[text.Length - 1] != ' ')
                        text.Append(' ');
                    text.Append(char.ToLowerInvariant(raw));
                    previousLower = char.IsLower(raw) || char.IsDigit(raw);
                }
                else
                {
                    if (text.Length != 0 && text[text.Length - 1] != ' ') text.Append(' ');
                    previousLower = false;
                }
            }
            return string.Join(" ", Tokens(text.ToString()));
        }

        internal static string PrototypeQuery(string value)
        {
            return string.Join(" ", Tokens(Normalize(value))
                .Where(token => !Noise.Contains(token, StringComparer.Ordinal)));
        }

        private static string[] Tokens(string value) => value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

        private static string Category(string normalized)
        {
            foreach (var category in Categories)
                if (category.Aliases.Any(alias => normalized.IndexOf(alias, StringComparison.Ordinal) >= 0))
                    return category.Name;
            return null;
        }

        private static double Similarity(string first, string second)
        {
            if (first.Length == 0 || second.Length == 0) return 0;
            var previous = new int[second.Length + 1];
            var current = new int[second.Length + 1];
            for (int j = 0; j <= second.Length; j++) previous[j] = j;
            for (int i = 1; i <= first.Length; i++)
            {
                current[0] = i;
                for (int j = 1; j <= second.Length; j++)
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1),
                        previous[j - 1] + (first[i - 1] == second[j - 1] ? 0 : 1));
                var swap = previous;
                previous = current;
                current = swap;
            }
            return 1.0 - (double)previous[second.Length] / Math.Max(first.Length, second.Length);
        }
    }
}
