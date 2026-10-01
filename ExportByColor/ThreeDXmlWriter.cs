using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace TxTools.ExportByColor
{
    /// <summary>Incremental 3DXML archive writer with Process Simulate hierarchy preservation.</summary>
    public sealed class ThreeDXmlWriter : IDisposable
    {
        internal sealed class PartTicket
        {
            internal string DisplayName;
            internal string MemberName;
            internal string Identity;
            internal string[] ParentPath;
            internal bool Added;
        }

        private sealed class AssemblyNode
        {
            internal readonly string Name;
            internal readonly List<AssemblyNode> Children = new List<AssemblyNode>();
            internal readonly Dictionary<string, AssemblyNode> ChildrenByName =
                new Dictionary<string, AssemblyNode>(StringComparer.Ordinal);
            internal readonly List<PartTicket> Parts = new List<PartTicket>();

            internal AssemblyNode(string name) { Name = name; }

            internal AssemblyNode GetOrAdd(string name)
            {
                AssemblyNode value;
                if (ChildrenByName.TryGetValue(name, out value)) return value;
                value = new AssemblyNode(name);
                ChildrenByName.Add(name, value);
                Children.Add(value);
                return value;
            }
        }

        private const string Namespace3DXml = "http://www.3ds.com/xsd/3DXML";
        private const string NamespaceXsi = "http://www.w3.org/2001/XMLSchema-instance";
        private const string NamespaceXlink = "http://www.w3.org/1999/xlink";
        private const string IdentityMatrix = "1 0 0 0 1 0 0 0 1 0 0 0";

        private readonly string _outputPath;
        private readonly string _partialPath;
        private readonly string _title;
        private readonly string _rootMember;
        private readonly string _identitySeed = Guid.NewGuid().ToString("N");
        private readonly string _memberPrefix;
        private readonly FileStream _file;
        private ZipArchive _archive;
        private readonly Dictionary<string, int> _memberCounts =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<ushort> _usedDiscriminators = new HashSet<ushort>();
        private readonly HashSet<uint> _usedTokens = new HashSet<uint>();
        private readonly List<PartTicket> _parts = new List<PartTicket>();
        private bool _completed;
        private bool _disposed;

        public ThreeDXmlWriter(string outputPath, string title)
        {
            if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentNullException("outputPath");
            _outputPath = Path.GetFullPath(outputPath);
            _title = string.IsNullOrWhiteSpace(title) ? "DirectCGRAssembly" : title.Trim();
            _rootMember = ArchiveStem(_title) + ".3dxml";
            _memberPrefix = "R" + _identitySeed.Substring(0, 8) + "_";
            string directory = Path.GetDirectoryName(_outputPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                throw new DirectoryNotFoundException("3DXML 输出目录不存在: " + directory);
            _partialPath = _outputPath + "." + Guid.NewGuid().ToString("N") + ".partial";
            _file = new FileStream(_partialPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            _archive = new ZipArchive(_file, ZipArchiveMode.Create, true, Encoding.UTF8);
        }

        public int PartCount { get { return _parts.Count; } }
        public string OutputPath { get { return _outputPath; } }

        internal PartTicket PreparePart(string exportName, IList<string> parentPath)
        {
            ThrowIfClosed();
            string stem = ArchiveStem(exportName);
            int count;
            _memberCounts.TryGetValue(stem, out count);
            _memberCounts[stem] = count + 1;
            string unique = count == 0 ? stem : stem + "_" + (count + 1).ToString(CultureInfo.InvariantCulture);
            // CATIA caches associated representation names for the entire session.
            // Package-local prefixes prevent two simultaneously open 3DXML files
            // with the same Process Simulate device names from aliasing each other.
            string member = _memberPrefix + unique + ".3DRep";

            int nonce = 0;
            string identity;
            while (true)
            {
                string localIdentity = nonce == 0 ? member : member + "#" + nonce.ToString(CultureInfo.InvariantCulture);
                identity = _identitySeed + "|" + localIdentity;
                ushort discriminator; uint token;
                Cfv3Encoder.ShellIdentityValues(identity, out discriminator, out token);
                if (!_usedDiscriminators.Contains(discriminator) && !_usedTokens.Contains(token))
                {
                    _usedDiscriminators.Add(discriminator);
                    _usedTokens.Add(token);
                    break;
                }
                nonce++;
            }

            string[] path = new string[parentPath == null ? 0 : parentPath.Count];
            for (int i = 0; i < path.Length; i++) path[i] = string.IsNullOrWhiteSpace(parentPath[i]) ? "未命名层级" : parentPath[i].Trim();
            return new PartTicket
            {
                DisplayName = string.IsNullOrWhiteSpace(exportName) ? unique : exportName.Trim(),
                MemberName = member,
                Identity = identity,
                ParentPath = path
            };
        }

        internal void AddEncodedPart(PartTicket ticket, string representationPath)
        {
            ThrowIfClosed();
            if (ticket == null) throw new ArgumentNullException("ticket");
            if (ticket.Added) throw new InvalidOperationException("3DXML part 已写入: " + ticket.MemberName);
            if (!File.Exists(representationPath)) throw new FileNotFoundException("3DRep 临时文件不存在", representationPath);
            ZipArchiveEntry entry = _archive.CreateEntry(ticket.MemberName, CompressionLevel.Optimal);
            using (Stream target = entry.Open())
            using (var source = new FileStream(representationPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                source.CopyTo(target, 1024 * 1024);
            ticket.Added = true;
            _parts.Add(ticket);
        }

        public void Complete()
        {
            ThrowIfClosed();
            if (_parts.Count == 0) throw new InvalidOperationException("没有可写入 3DXML 的表示数据");
            WriteEntry("Manifest.xml", BuildManifestXml());
            WriteEntry(_rootMember, BuildProductXml());
            _archive.Dispose();
            _archive = null;
            _file.Flush(true);
            _file.Dispose();
            if (File.Exists(_outputPath)) File.Delete(_outputPath);
            File.Move(_partialPath, _outputPath);
            _completed = true;
        }

        private void WriteEntry(string name, byte[] data)
        {
            ZipArchiveEntry entry = _archive.CreateEntry(name, CompressionLevel.Optimal);
            using (Stream stream = entry.Open()) stream.Write(data, 0, data.Length);
        }

        private byte[] BuildManifestXml()
        {
            return WriteXml(delegate(XmlWriter writer)
            {
                writer.WriteStartElement("Manifest");
                writer.WriteAttributeString("xmlns", "xsi", null, NamespaceXsi);
                writer.WriteAttributeString("xsi", "noNamespaceSchemaLocation", NamespaceXsi, "Manifest.xsd");
                writer.WriteElementString("Root", _rootMember);
                writer.WriteElementString("Date", DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                writer.WriteElementString("WithAuthoringData", "false");
                writer.WriteEndElement();
            });
        }

        private byte[] BuildProductXml()
        {
            var rootNode = new AssemblyNode(_title);
            foreach (PartTicket part in _parts)
            {
                AssemblyNode node = rootNode;
                foreach (string segment in part.ParentPath) node = node.GetOrAdd(segment);
                node.Parts.Add(part);
            }

            return WriteXml(delegate(XmlWriter writer)
            {
                writer.WriteStartElement("Model_3dxml", Namespace3DXml);
                writer.WriteAttributeString("xmlns", "xsi", null, NamespaceXsi);
                writer.WriteAttributeString("xmlns", "xlink", null, NamespaceXlink);
                writer.WriteStartElement("Header", Namespace3DXml);
                writer.WriteElementString("SchemaVersion", Namespace3DXml, "4.3");
                writer.WriteElementString("Title", Namespace3DXml, _title);
                writer.WriteElementString("Author", Namespace3DXml, "TxTools CFV3 encoder");
                writer.WriteElementString("Generator", Namespace3DXml, "TxTools direct CGR to 3DXML");
                writer.WriteElementString("Created", Namespace3DXml, DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                writer.WriteEndElement();

                writer.WriteStartElement("ProductStructure", Namespace3DXml);
                writer.WriteAttributeString("root", "1");
                WriteReference3D(writer, 1, _title);
                int nextId = 2;
                foreach (AssemblyNode child in rootNode.Children) EmitNode(writer, 1, child, ref nextId);
                foreach (PartTicket part in rootNode.Parts) EmitPart(writer, 1, part, ref nextId);
                writer.WriteEndElement();
                writer.WriteEndElement();
            });
        }

        private static void EmitNode(XmlWriter writer, int parentId, AssemblyNode node, ref int nextId)
        {
            int instanceId = nextId++;
            int referenceId = nextId++;
            WriteInstance3D(writer, instanceId, node.Name + ".1", parentId, referenceId);
            WriteReference3D(writer, referenceId, node.Name);
            foreach (AssemblyNode child in node.Children) EmitNode(writer, referenceId, child, ref nextId);
            foreach (PartTicket part in node.Parts) EmitPart(writer, referenceId, part, ref nextId);
        }

        private static void EmitPart(XmlWriter writer, int parentId, PartTicket part, ref int nextId)
        {
            int instanceId = nextId++;
            int referenceId = nextId++;
            int instanceRepId = nextId++;
            int referenceRepId = nextId++;
            string repName = part.DisplayName + "_ReferenceRep";
            WriteInstance3D(writer, instanceId, part.DisplayName + ".1", parentId, referenceId);
            WriteReference3D(writer, referenceId, part.DisplayName);

            writer.WriteStartElement("InstanceRep", Namespace3DXml);
            writer.WriteAttributeString("xsi", "type", NamespaceXsi, "InstanceRepType");
            writer.WriteAttributeString("id", instanceRepId.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("name", part.DisplayName + "_InstanceRep");
            writer.WriteElementString("IsAggregatedBy", Namespace3DXml, referenceId.ToString(CultureInfo.InvariantCulture));
            writer.WriteElementString("IsInstanceOf", Namespace3DXml, referenceRepId.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndElement();

            writer.WriteStartElement("ReferenceRep", Namespace3DXml);
            writer.WriteAttributeString("xsi", "type", NamespaceXsi, "ReferenceRepType");
            writer.WriteAttributeString("id", referenceRepId.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("name", repName);
            writer.WriteAttributeString("format", "UVR");
            writer.WriteAttributeString("version", "1.0");
            writer.WriteAttributeString("associatedFile", "urn:3DXML:" + part.MemberName);
            writer.WriteElementString("PLM_ExternalID", Namespace3DXml, repName);
            writer.WriteElementString("V_discipline", Namespace3DXml, "Design");
            writer.WriteElementString("V_usage", Namespace3DXml, "3DShape");
            writer.WriteElementString("V_nature", Namespace3DXml, "1");
            writer.WriteEndElement();
        }

        private static void WriteInstance3D(XmlWriter writer, int id, string name, int parentId, int referenceId)
        {
            writer.WriteStartElement("Instance3D", Namespace3DXml);
            writer.WriteAttributeString("xsi", "type", NamespaceXsi, "Instance3DType");
            writer.WriteAttributeString("id", id.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("name", name);
            writer.WriteElementString("IsAggregatedBy", Namespace3DXml, parentId.ToString(CultureInfo.InvariantCulture));
            writer.WriteElementString("IsInstanceOf", Namespace3DXml, referenceId.ToString(CultureInfo.InvariantCulture));
            writer.WriteElementString("RelativeMatrix", Namespace3DXml, IdentityMatrix);
            writer.WriteEndElement();
        }

        private static void WriteReference3D(XmlWriter writer, int id, string name)
        {
            writer.WriteStartElement("Reference3D", Namespace3DXml);
            writer.WriteAttributeString("xsi", "type", NamespaceXsi, "Reference3DType");
            writer.WriteAttributeString("id", id.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("name", name);
            writer.WriteElementString("PLM_ExternalID", Namespace3DXml, name);
            writer.WriteEndElement();
        }

        private static byte[] WriteXml(Action<XmlWriter> write)
        {
            using (var stream = new MemoryStream())
            {
                var settings = new XmlWriterSettings
                {
                    Encoding = new UTF8Encoding(false),
                    Indent = true,
                    IndentChars = "\t",
                    NewLineChars = "\n",
                    NewLineHandling = NewLineHandling.Replace,
                    CloseOutput = false
                };
                using (XmlWriter writer = XmlWriter.Create(stream, settings))
                {
                    writer.WriteStartDocument();
                    write(writer);
                    writer.WriteEndDocument();
                }
                return stream.ToArray();
            }
        }

        private static string ArchiveStem(string value)
        {
            // Callers already pass a logical stem. Do not treat dotted PS names
            // such as "2026.2.11" as if ".11" were a file extension.
            string source = value ?? "";
            string result = Regex.Replace(source, "[^0-9A-Za-z_.-]+", "_").Trim('.', '_');
            return result.Length == 0 ? "Part" : result;
        }

        private void ThrowIfClosed()
        {
            if (_disposed || _completed || _archive == null) throw new ObjectDisposedException("ThreeDXmlWriter");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_archive != null) { try { _archive.Dispose(); } catch { } _archive = null; }
            try { _file.Dispose(); } catch { }
            if (!_completed && File.Exists(_partialPath))
            {
                try { File.Delete(_partialPath); } catch { }
            }
        }
    }
}
