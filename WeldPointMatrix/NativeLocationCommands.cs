using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

namespace TxTools.WeldPointMatrix
{
    internal static class NativeLocationCommands
    {
        internal static string Resolve(bool after, IEnumerable<string> roots)
        {
            string configured = Environment.GetEnvironmentVariable(after
                ? "TXTOOLS_ADD_LOCATION_AFTER_COMMAND" : "TXTOOLS_ADD_LOCATION_BEFORE_COMMAND");
            if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
            foreach (string root in roots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct())
                foreach (string path in RibbonFiles(root))
                {
                    string command = ReadRibbon(path, after);
                    if (command != null) return command;
                }

            throw new InvalidOperationException("未在 PS Ribbon 配置中找到“" +
                (after ? "后插过渡点" : "前插过渡点") + "”，请检查本机 RibbonConfiguration.xml 中的命令 ID。");
        }

        internal static string ReadRibbon(string path, bool after)
        {
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                var document = new XmlDocument { XmlResolver = null };
                using (var reader = XmlReader.Create(path, settings)) document.Load(reader);
                foreach (XmlElement element in document.SelectNodes("//*[local-name()='RibbonItem' or local-name()='Command']"))
                {
                    string name = element.GetAttribute("Name");
                    if (string.IsNullOrEmpty(name)) name = element.GetAttribute("Text");
                    if (string.IsNullOrEmpty(name)) name = element.GetAttribute("DisplayName");
                    if (string.IsNullOrEmpty(name))
                    {
                        XmlNode previous = element.PreviousSibling;
                        while (previous != null && (previous.NodeType == XmlNodeType.Whitespace
                            || previous.NodeType == XmlNodeType.SignificantWhitespace)) previous = previous.PreviousSibling;
                        if (previous is XmlComment comment)
                        {
                            string text = comment.Value.Trim();
                            if (text.StartsWith("Name:", StringComparison.OrdinalIgnoreCase)) name = text.Substring(5).Trim();
                        }
                    }
                    if (!Matches(name, after)) continue;
                    // IDs are opaque: COM GUIDs and managed command names are both valid.
                    string id = element.GetAttribute("Id");
                    if (string.IsNullOrEmpty(id)) id = element.GetAttribute("ID");
                    if (string.IsNullOrEmpty(id)) id = element.GetAttribute("CommandId");
                    if (!string.IsNullOrWhiteSpace(id)) return id.Trim();
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (XmlException) { }
            return null;
        }

        private static bool Matches(string value, bool after)
        {
            string normalized = new string((value ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            return normalized == (after ? "addlocationafter" : "addlocationbefore")
                || normalized == (after ? "后插过渡点" : "前插过渡点");
        }

        private static IEnumerable<string> RibbonFiles(string root)
        {
            var pending = new Queue<Tuple<string, int>>();
            pending.Enqueue(Tuple.Create(root, 0));
            while (pending.Count != 0)
            {
                var entry = pending.Dequeue();
                string[] files = new string[0], directories = new string[0];
                try
                {
                    files = Directory.GetFiles(entry.Item1, "RibbonConfiguration*.xml");
                    if (entry.Item2 < 4) directories = Directory.GetDirectories(entry.Item1);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                foreach (string file in files) yield return file;
                foreach (string directory in directories)
                {
                    bool linked;
                    try { linked = (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0; }
                    catch { continue; }
                    if (!linked) pending.Enqueue(Tuple.Create(directory, entry.Item2 + 1));
                }
            }
        }
    }
}
