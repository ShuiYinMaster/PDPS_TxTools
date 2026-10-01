using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Tecnomatix.Engineering;

namespace TxTools.ExportByColor
{
    /// <summary>
    /// Resolves the JT backing file of a Process Simulate resource without
    /// depending on one particular Tecnomatix SDK version.  SDK releases have
    /// exposed the backing path through different StorageObject/representation
    /// property names, so the resolver is deliberately reflection based and
    /// bounded to a small object graph.
    /// </summary>
    internal static class JtResourceResolver
    {
        private sealed class WorkItem
        {
            internal object Value;
            internal string Label;
            internal int Depth;
            internal WorkItem(object value, string label, int depth)
            {
                Value = value;
                Label = label;
                Depth = depth;
            }
        }

        private static readonly string[] PathProperties =
        {
            "JtPath", "JTPath", "JtFile", "JTFile", "FilePath", "FullPath",
            "FileName", "Path", "Location", "SourcePath", "SourceFile",
            "ResourceFile", "ResourcePath", "RepresentationPath", "StoragePath",
            "NativeFile", "NativePath", "File", "DocumentPath"
        };

        private static readonly string[] ObjectProperties =
        {
            "StorageObject", "Storage", "Representation", "Resource",
            "Source", "FileInfo", "Location", "NativeRepresentation",
            "Document", "File"
        };

        internal static bool TryResolve(ITxObject resource, out string path, out string trace)
        {
            path = null;
            trace = "";
            if (resource == null)
            {
                trace = "resource=null";
                return false;
            }

            var queue = new Queue<WorkItem>();
            var seen = new HashSet<int>();
            string nonExisting = null;
            queue.Enqueue(new WorkItem(resource, "resource", 0));
            while (queue.Count > 0)
            {
                WorkItem item = queue.Dequeue();
                if (item.Value == null || item.Depth > 3) continue;
                int identity;
                try { identity = RuntimeHelpers.GetHashCode(item.Value); }
                catch { identity = item.Value.GetHashCode(); }
                if (!seen.Add(identity)) continue;

                foreach (string propertyName in PathProperties)
                {
                    object value = ReadProperty(item.Value, propertyName);
                    if (value == null) continue;
                    string candidate = Candidate(value);
                    if (string.IsNullOrEmpty(candidate)) continue;
                    trace = item.Label + "." + propertyName;
                    if (File.Exists(candidate))
                    {
                        path = candidate;
                        return true;
                    }
                    if (nonExisting == null && candidate.EndsWith(".jt", StringComparison.OrdinalIgnoreCase))
                        nonExisting = candidate;
                }

                if (item.Depth == 3) continue;
                foreach (string propertyName in ObjectProperties)
                {
                    object value = ReadProperty(item.Value, propertyName);
                    if (value == null || value is string) continue;
                    queue.Enqueue(new WorkItem(value, item.Label + "." + propertyName, item.Depth + 1));
                }
            }

            if (nonExisting != null)
            {
                path = nonExisting;
                trace = trace + " (文件尚未在本机确认存在)";
                return true;
            }
            trace = "未找到 JT 路径";
            return false;
        }

        private static object ReadProperty(object value, string name)
        {
            try
            {
                PropertyInfo property = value.GetType().GetProperty(
                    name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                if (property == null || property.GetIndexParameters().Length != 0 || !property.CanRead) return null;
                return property.GetValue(value, null);
            }
            catch { return null; }
        }

        private static string Candidate(object value)
        {
            string text = value as string;
            if (text == null)
            {
                try
                {
                    PropertyInfo fullName = value.GetType().GetProperty("FullName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                    if (fullName != null && fullName.PropertyType == typeof(string)) text = fullName.GetValue(value, null) as string;
                }
                catch { }
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                try { text = value.ToString(); } catch { text = null; }
            }
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim().Trim('"');
            if (text.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                text = text.Substring("file://".Length);
            try { text = Path.GetFullPath(text); } catch { }
            return text.EndsWith(".jt", StringComparison.OrdinalIgnoreCase) ? text : null;
        }
    }
}
