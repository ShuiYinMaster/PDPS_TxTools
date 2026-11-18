using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using TxTools.CatiaPartTree.Conversion;
using TxTools.Common.TreeWorkflow;

namespace TxTools.CatiaPartTree.Core
{
    internal sealed class ThreeDXmlFileRequiredException : IOException
    {
        internal ThreeDXmlFileRequiredException(string name)
            : base("CATIA 中的 " + name + " 是内存产品，请选择对应的原始 3DXML 文件读取结构。") { }
    }

    internal static class ThreeDXmlSource
    {
        private static readonly XNamespace Ns = "http://www.3ds.com/xsd/3DXML";
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
        internal static CatiaImportSource Read(string path)
        {
            path = Path.GetFullPath(path);
            using (var file = File.OpenRead(path))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Read))
            {
                var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in archive.Entries)
                {
                    string name = e.FullName.Replace('\\', '/');
                    if (entries.ContainsKey(name)) throw new InvalidDataException("3DXML 包内文件名重复：" + name);
                    entries.Add(name, e);
                }
                ZipArchiveEntry manifest;
                entries.TryGetValue("Manifest.xml", out manifest);
                var manifestRoot = ReadXml(manifest).Root;
                string rootFile = Required(manifestRoot?.Elements().FirstOrDefault(e => e.Name.LocalName == "Root")?.Value, "Manifest/Root");
                ZipArchiveEntry rootEntry;
                entries.TryGetValue(EntryName(rootFile), out rootEntry);
                var structure = ReadXml(rootEntry).Root?.Element(Ns + "ProductStructure");
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
                    geometry[parent].Add(representations[target]);
                }
                string rootId = Required((string)structure.Attribute("root"), "根引用 ID");
                if (!references.ContainsKey(rootId)) throw new InvalidDataException("3DXML 根引用不存在：" + rootId);
                var source = new CatiaImportSource { DocumentPath = path };
                int count = 0;
                Func<string, string, string, Frame12, HashSet<string>, StructureNode> expand = null;
                expand = (id, instanceName, instancePath, frame, ancestors) =>
                {
                    if (ancestors.Count >= 100 || !ancestors.Add(id)) throw new InvalidDataException("3DXML 存在循环引用或层级超过 100。");
                    if (++count > 100000) throw new InvalidDataException("3DXML 展开实例超过 100000。");
                    var reference = references[id];
                    bool hasGeometry = geometry[id].Count > 0;
                    var node = new StructureNode { Key = "3dxml/" + instancePath,
                        SourceName = instanceName ?? (string)reference.Attribute("name") ?? id,
                        PartNumber = (string)reference.Element(Ns + "PLM_ExternalID") ?? (string)reference.Attribute("name"),
                        IsContainer = children[id].Count > 0 || !hasGeometry };
                    // Geometry owned by an assembly becomes a separate part beneath it.
                    // This preserves both its own representation and its child products.
                    if (hasGeometry)
                    {
                        var partNode = node;
                        if (node.IsContainer)
                        {
                            partNode = new StructureNode { Key = node.Key + "/geometry", SourceName = node.SourceName + " · 几何", PartNumber = node.PartNumber };
                            node.Add(partNode);
                        }
                        string[] files = geometry[id].Select(e => EntryName((string)e.Attribute("associatedFile"))).ToArray();
                        string missing = string.Join("、", files.Where(n => !entries.ContainsKey(n)));
                        string issue = missing.Length > 0 ? "3DXML 缺少几何文件：" + missing :
                            "3DXML 几何表示（" + string.Join("/", geometry[id].Select(e => (string)e.Attribute("format")).Distinct()) +
                            "）已识别；当前转换器仅支持 CATPart。可创建复合零件或分配已有零件。";
                        source.Parts.Add(partNode.Key, new CatiaSourcePart { File = path, Frame = frame.Components,
                            ReferenceId = id, InstancePath = instancePath, GeometryEntries = files, ConversionIssue = issue });
                    }
                    foreach (var child in children[id])
                    {
                        string matrix = (string)child.Element(Ns + "RelativeMatrix");
                        var local = string.IsNullOrWhiteSpace(matrix) ? Frame12.Identity : new Frame12(matrix.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
                            .Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray());
                        node.Add(expand(Link(child, "IsInstanceOf"), (string)child.Attribute("name"), instancePath + "/" + Id(child), frame.ThenChild(local), ancestors));
                    }
                    ancestors.Remove(id); return node;
                };
                source.Root = expand(rootId, null, rootId, Frame12.Identity, new HashSet<string>());
                source.Files[path] = CatiaImportSource.Hash(path);
                return source;
            }
        }
    }
}
