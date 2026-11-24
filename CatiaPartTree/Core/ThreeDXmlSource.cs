using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using TxTools.CatiaPartTree.Conversion;
using TxTools.Common.TreeWorkflow;

namespace TxTools.CatiaPartTree.Core
{
    internal sealed class ThreeDXmlFileRequiredException : IOException
    {
        internal string FileName { get; }
        internal string Directory { get; }
        internal ThreeDXmlFileRequiredException(string name, string directory)
            : base("CATIA 未提供可读取的 3DXML 路径，请选择原始文件：" + name)
        {
            FileName = name;
            Directory = directory;
        }
    }

    internal static class ThreeDXmlSource
    {
        private static readonly XNamespace Ns = "http://www.3ds.com/xsd/3DXML";
        private static readonly Regex NumberedCgrName = new Regex(@"\Acgr[0-9]+(?:_[0-9]+)*\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static string Id(XElement e) { return Required(e.Attribute("id")?.Value, "节点 ID"); }
        private static string Required(string value, string label)
        { if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("3DXML 缺少" + label + "。"); return value.Trim(); }
        private static string Link(XElement e, string name) { return Required((string)e.Element(Ns + name), name); }
        private static XDocument ReadXml(ZipArchiveEntry entry)
        {
            if (entry == null) throw new InvalidDataException("3DXML 缺少 XML 结构文件。");
            using (var stream = entry.Open())
            using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 })) return XDocument.Load(reader);
        }
        private static Dictionary<string, XElement> Index(IEnumerable<XElement> elements)
        {
            var result = new Dictionary<string, XElement>(StringComparer.Ordinal);
            foreach (var e in elements)
            {
                string id = Id(e);
                if (result.ContainsKey(id)) throw new InvalidDataException("3DXML 节点 ID 重复：" + id);
                result.Add(id, e);
            }
            return result;
        }
        private static string EntryName(string value)
        {
            const string prefix = "urn:3DXML:";
            value = Required(value, "几何文件引用");
            return (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? value.Substring(prefix.Length) : value).Replace('\\', '/');
        }
        internal static string GeometryStem(string entry)
        {
            string name = entry.Replace('\\', '/');
            name = name.Substring(name.LastIndexOf('/') + 1);
            int dot = name.LastIndexOf('.');
            return CatiaLibraryDestination.FolderSegment(dot > 0 ? name.Substring(0, dot) : name);
        }
        internal static string GeometryOutputStem(string member, string partNumber, string instanceName)
        {
            string stem = GeometryStem(member);
            if (!NumberedCgrName.IsMatch(stem)) return stem;
            string name = string.IsNullOrWhiteSpace(partNumber) ? instanceName : partNumber;
            return string.IsNullOrWhiteSpace(name) ? stem : CatiaLibraryDestination.FolderSegment(name);
        }
        private static string GeometryIssue(ZipArchiveEntry entry, string name, string format, bool allowXmlGeometry)
        {
            if (entry == null) return "3DXML 缺少包内几何：" + name;
            try
            {
                var header = new byte[8];
                using (var stream = entry.Open())
                {
                    int offset = 0, read;
                    while (offset < header.Length && (read = stream.Read(header, offset, header.Length - offset)) != 0) offset += read;
                    if (offset == header.Length)
                    {
                        string magic = Encoding.ASCII.GetString(header);
                        if (magic == "V5_CFV2\0" || magic == "V5_CFV3\0") return null;
                    }
                }
                if (allowXmlGeometry && IsXmlRepresentation(name)) return null;
                return "3DXML 表示格式非 CGR（" + format + "）：" + name;
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is NotSupportedException)
            { return "3DXML 几何无法读取：" + name + "；" + error.Message; }
        }
        private static Frame12 LocalFrame(XElement instance)
        {
            string matrix = (string)instance.Element(Ns + "RelativeMatrix");
            return string.IsNullOrWhiteSpace(matrix) ? Frame12.Identity : new Frame12(matrix.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray());
        }
        internal static void ExtractGeometry(string archivePath, string member, string output, CancellationToken cancel)
        {
            using (var file = File.OpenRead(archivePath))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Read))
                ExtractGeometry(archive, member, output, cancel);
        }

        internal static void ExtractGeometry(ZipArchive archive, string member, string output, CancellationToken cancel)
        {
            var entry = archive.GetEntry(member);
            if (entry == null) throw new IOException("3DXML 缺少包内几何：" + member);
            using (var source = entry.Open())
            using (var destination = new FileStream(output, FileMode.CreateNew, FileAccess.Write))
            {
                var buffer = new byte[128 * 1024];
                int count;
                while ((count = source.Read(buffer, 0, buffer.Length)) != 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    destination.Write(buffer, 0, count);
                }
                if (destination.Length != entry.Length) throw new InvalidDataException("3DXML 几何长度不匹配：" + member);
            }
        }
        internal static bool IsXmlRepresentation(string member) =>
            member.EndsWith(".3drep", StringComparison.OrdinalIgnoreCase);

        private static HashSet<string> HiddenOccurrences(XElement model, string rootFile, string rootId)
        {
            var hidden = new HashSet<string>(StringComparer.Ordinal);
            var properties = model?.Element(Ns + "DefaultView")?.Elements(Ns + "DefaultViewProperty");
            if (properties == null) return hidden;
            foreach (var property in properties)
            {
                string visibility = ((string)property.Element(Ns + "GraphicProperties")?.Element(Ns + "GeneralAttributes")
                    ?.Attribute("visible"))?.Trim();
                if (visibility != "false" && visibility != "0" && visibility != "true" && visibility != "1") continue;
                var path = new List<string>();
                foreach (var link in property.Element(Ns + "OccurenceId")?.Elements(Ns + "id") ?? Enumerable.Empty<XElement>())
                {
                    string value = link.Value.Trim();
                    int fragment = value.LastIndexOf('#');
                    if (fragment > 0)
                    {
                        string file = EntryName(value.Substring(0, fragment));
                        if (!file.Equals(rootFile, StringComparison.OrdinalIgnoreCase) &&
                            !file.Equals(Path.GetFileName(rootFile), StringComparison.OrdinalIgnoreCase))
                        {
                            path.Clear();
                            break;
                        }
                    }
                    string id = value.Substring(fragment + 1);
                    if (id.Length == 0) { path.Clear(); break; }
                    path.Add(id);
                }
                if (path.Count == 0) continue;
                string occurrence = rootId + "/" + string.Join("/", path);
                if (visibility == "false" || visibility == "0") hidden.Add(occurrence);
                else hidden.Remove(occurrence);
            }
            return hidden;
        }

        internal static CatiaImportSource Read(string path, CancellationToken cancel = default(CancellationToken),
            bool allowXmlGeometry = false, bool readVisibility = false)
        {
            path = Path.GetFullPath(path);
            using (var file = File.OpenRead(path))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Read))
            {
                var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in archive.Entries)
                {
                    cancel.ThrowIfCancellationRequested();
                    string name = e.FullName.Replace('\\', '/');
                    if (entries.ContainsKey(name)) throw new InvalidDataException("3DXML 包内文件名重复：" + name);
                    entries.Add(name, e);
                }
                ZipArchiveEntry manifest;
                entries.TryGetValue("Manifest.xml", out manifest);
                var manifestRoot = ReadXml(manifest).Root;
                string rootFile = EntryName(Required(manifestRoot?.Elements().FirstOrDefault(e => e.Name.LocalName == "Root")?.Value, "Manifest/Root"));
                ZipArchiveEntry rootEntry;
                entries.TryGetValue(rootFile, out rootEntry);
                var model = ReadXml(rootEntry).Root;
                var structure = model?.Element(Ns + "ProductStructure");
                if (structure == null) throw new InvalidDataException("3DXML 缺少 ProductStructure。");
                var references = Index(structure.Elements(Ns + "Reference3D"));
                var representations = Index(structure.Elements(Ns + "ReferenceRep"));
                var instances = Index(structure.Elements(Ns + "Instance3D"));
                var repInstances = Index(structure.Elements(Ns + "InstanceRep"));
                var children = references.Keys.ToDictionary(id => id, id => new List<XElement>());
                var geometry = references.Keys.ToDictionary(id => id, id => new List<XElement>());
                foreach (var e in instances.Values)
                {
                    string parent = Link(e, "IsAggregatedBy"), target = Link(e, "IsInstanceOf");
                    if (!references.ContainsKey(parent) || !references.ContainsKey(target)) throw new InvalidDataException("3DXML 产品引用不存在：" + Id(e));
                    children[parent].Add(e);
                }
                foreach (var e in repInstances.Values)
                {
                    string parent = Link(e, "IsAggregatedBy"), target = Link(e, "IsInstanceOf");
                    if (!references.ContainsKey(parent) || !representations.ContainsKey(target)) throw new InvalidDataException("3DXML 几何引用不存在：" + Id(e));
                    geometry[parent].Add(e);
                }
                string rootId = Required((string)structure.Attribute("root"), "根引用 ID");
                if (!references.ContainsKey(rootId)) throw new InvalidDataException("3DXML 根引用不存在：" + rootId);
                var hiddenOccurrences = readVisibility ? HiddenOccurrences(model, rootFile, rootId) : null;
                var source = new CatiaImportSource { DocumentPath = path };
                var issues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                int count = 0;
                StructureNode Expand(string id, string instanceName, string instancePath, Frame12 frame, HashSet<string> ancestors, bool parentHidden)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (ancestors.Count >= 100 || !ancestors.Add(id)) throw new InvalidDataException("3DXML 存在循环引用或层级超过 100。");
                    if (++count > 100000) throw new InvalidDataException("3DXML 展开实例超过 100000。");
                    bool hidden = parentHidden || hiddenOccurrences?.Contains(instancePath) == true;
                    var reference = references[id];
                    string partNumber = (string)reference.Element(Ns + "PLM_ExternalID");
                    if (string.IsNullOrWhiteSpace(partNumber)) partNumber = (string)reference.Attribute("name");
                    var node = new StructureNode { Key = "3dxml/" + instancePath,
                        SourceName = instanceName ?? (string)reference.Attribute("name") ?? id,
                        PartNumber = partNumber,
                        IsContainer = children[id].Count > 0 || geometry[id].Count != 1 };
                    // Geometry owned by an assembly becomes a separate part beneath it.
                    // This preserves both its own representation and its child products.
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var repInstance in geometry[id])
                    {
                        cancel.ThrowIfCancellationRequested();
                        if (++count > 100000) throw new InvalidDataException("3DXML 展开实例超过 100000。");
                        var representation = representations[Link(repInstance, "IsInstanceOf")];
                        string member = EntryName((string)representation.Attribute("associatedFile"));
                        entries.TryGetValue(member, out var entry);
                        if (!issues.TryGetValue(member, out string issue))
                        {
                            issue = GeometryIssue(entry, member, (string)representation.Attribute("format"), allowXmlGeometry);
                            issues.Add(member, issue);
                        }
                        var partNode = node;
                        string partPath = instancePath;
                        if (node.IsContainer)
                        {
                            string stem = geometry[id].Count == 1 ? node.SourceName + " · 几何" : GeometryStem(member);
                            string name = stem;
                            for (int version = 2; !names.Add(name); version++) name = stem + "_" + version;
                            partPath += "/geometry/" + Id(repInstance);
                            partNode = new StructureNode { Key = "3dxml/" + partPath, SourceName = name, PartNumber = node.PartNumber };
                            node.Add(partNode);
                        }
                        source.Parts.Add(partNode.Key, new CatiaSourcePart { File = path, Frame = frame.ThenChild(LocalFrame(repInstance)).Components,
                            ReferenceId = id, InstancePath = partPath, GeometryEntries = new[] { entry?.FullName ?? member }, ConversionIssue = issue,
                            Hidden = hidden || hiddenOccurrences?.Contains(instancePath + "/" + Id(repInstance)) == true });
                    }
                    foreach (var child in children[id])
                    {
                        node.Add(Expand(Link(child, "IsInstanceOf"), (string)child.Attribute("name"), instancePath + "/" + Id(child),
                            frame.ThenChild(LocalFrame(child)), ancestors, hidden));
                    }
                    ancestors.Remove(id); return node;
                }
                source.Root = Expand(rootId, null, rootId, Frame12.Identity, new HashSet<string>(), false);
                source.Files[path] = CatiaImportSource.Hash(path);
                return source;
            }
        }
    }
}
