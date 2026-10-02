using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Tecnomatix.Engineering;

namespace TxTools.ExportByColor
{
    /// <summary>
    /// Resolves the JT backing file or native resource directory of a Process Simulate resource without
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
            , "ExternalFilePath", "ModelFilePath", "SourceFilePath",
            "GeometryFilePath", "ResourceFilePath", "ExternalFile",
            "FileLocation", "DataFilePath", "JtFilePath"
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
            string nonExistingTrace = null;
            // Match ExportGun/SRC/PsReader.TryGetToolStorageDir: SDK interface
            // access works even when StorageObject is implemented explicitly.
            var storable = resource as ITxStorable;
            if (storable != null)
            {
                try
                {
                    TxStorage storage = storable.StorageObject;
                    var library = storage as TxLibraryStorage;
                    if (library != null)
                    {
                        string candidate = Candidate(library.FullPath);
                        if (IsAvailable(candidate))
                        {
                            path = candidate;
                            trace = "ITxStorable.StorageObject → TxLibraryStorage.FullPath";
                            if (Directory.Exists(candidate)) trace += " (JT/COJT 资源目录，由 PS 原生加载)";
                            return true;
                        }
                        if (candidate != null)
                        {
                            nonExisting = candidate;
                            nonExistingTrace = "ITxStorable.StorageObject → TxLibraryStorage.FullPath";
                        }
                    }
                    if (storage != null) queue.Enqueue(new WorkItem(storage, "ITxStorable.StorageObject", 0));
                }
                catch (Exception ex) { trace = "StorageObject 读取失败: " + ex.Message; }
            }
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
                    string candidateTrace = item.Label + "." + propertyName;
                    if (IsAvailable(candidate))
                    {
                        path = candidate;
                        trace = candidateTrace;
                        if (Directory.Exists(candidate)) trace += " (JT/COJT 资源目录，由 PS 原生加载)";
                        return true;
                    }
                    if (nonExisting == null)
                    {
                        nonExisting = candidate;
                        nonExistingTrace = candidateTrace;
                    }
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
                trace = nonExistingTrace + " (JT 文件或 COJT 目录不存在)";
                return true;
            }
            trace = "未找到 JT/COJT 路径；资源类型=" + resource.GetType().FullName +
                (string.IsNullOrEmpty(trace) ? "" : "；" + trace);
            return false;
        }

        private static bool IsAvailable(string candidate)
        {
            return candidate != null && (File.Exists(candidate) || Directory.Exists(candidate));
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
            {
                Uri uri;
                if (!Uri.TryCreate(text, UriKind.Absolute, out uri) || !uri.IsFile) return null;
                text = uri.LocalPath;
            }
            text = Environment.ExpandEnvironmentVariables(text).TrimEnd('\\', '/');
            // Do not resolve a library-relative path against the plugin's working directory.
            if (!Path.IsPathRooted(text)) return null;
            try { text = Path.GetFullPath(text); } catch { return null; }
            return text.EndsWith(".jt", StringComparison.OrdinalIgnoreCase) ||
                text.EndsWith(".cojt", StringComparison.OrdinalIgnoreCase) ||
                Directory.Exists(text) ? text : null;
        }
    }
}
