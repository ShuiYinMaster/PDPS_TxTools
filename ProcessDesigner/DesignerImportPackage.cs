using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Tecnomatix.Engineering;
using TxTools.Agent.Core;
using TxTools.CatiaPartTree.Conversion;
using TxTools.CatiaPartTree.Core;
using TxTools.CatiaPartTree.Ps;
using TxTools.Common.TreeWorkflow;
using TxTools.CrossEnvIO;
using TxTools.ResourceImporter;
using TxTools.WeldPointImporter;

namespace TxTools.ProcessDesigner
{
    internal enum DesignerTree { Part, Resource, Mfg }

    internal sealed class DesignerImportFile
    {
        internal string FilePath { get; }
        internal IReadOnlyList<string> RootIds { get; }
        internal IReadOnlyList<string> ObjectIds { get; }

        internal DesignerImportFile(string path, IReadOnlyList<string> rootIds, IReadOnlyList<string> objectIds)
        {
            FilePath = path;
            RootIds = rootIds;
            ObjectIds = objectIds;
        }
    }

    internal sealed class DesignerImportPackage
    {
        internal string Name { get; }
        internal DesignerTree Tree { get; }
        internal DesignerImportFile Library { get; }
        internal DesignerImportFile Structure { get; }
        internal IReadOnlyDictionary<string, string> InstancePrototypes { get; }
        internal IReadOnlyList<string> Warnings { get; }
        internal int MissingGeometry { get; }

        internal DesignerImportPackage(string name, DesignerTree tree, DesignerImportFile library,
            DesignerImportFile structure = null, IReadOnlyDictionary<string, string> instancePrototypes = null,
            IReadOnlyList<string> warnings = null, int missingGeometry = 0)
        {
            Name = name;
            Tree = tree;
            Library = library;
            Structure = structure;
            InstancePrototypes = instancePrototypes;
            Warnings = warnings ?? Array.Empty<string>();
            MissingGeometry = missingGeometry;
        }
    }

    internal sealed class DesignerXml
    {
        private readonly string _prefix = "TXTOOLS-" + Guid.NewGuid().ToString("N");
        private readonly XElement _objects = new XElement("Objects");
        private readonly List<string> _expected = new List<string>();
        private int _nextId;

        internal static string Id(XElement element) => (string)element.Attribute("ExternalId");

        internal XElement Add(string type, string name, params object[] fields)
        {
            var item = new XElement(type, new XAttribute("ExternalId", _prefix + "-" + (++_nextId).ToString(CultureInfo.InvariantCulture)),
                new XElement("name", name), new XElement("status", "Open"), fields);
            _objects.Add(item);
            return item;
        }

        internal XElement AddTreeNode(string type, string name, params object[] fields)
        {
            var element = Add(type, name, fields);
            _expected.Add(Id(element));
            return element;
        }

        internal static XElement Reference(string field, XElement element) => new XElement(field, Id(element));
        internal static XElement List(string field, IEnumerable<XElement> items) =>
            new XElement(field, items.Select(item => new XElement("item", Id(item))));
        internal static XElement Vector(string field, params double[] values) => new XElement(field,
            values.Select(value => new XElement("item", value.ToString("R", CultureInfo.InvariantCulture))));

        internal DesignerImportFile Save(string path, IEnumerable<XElement> roots)
        {
            new XDocument(new XElement("Data", new XAttribute("PpXmlVer", "7.0"), _objects)).Save(path);
            return new DesignerImportFile(path, roots.Select(Id).ToArray(), _expected.ToArray());
        }

        internal static DesignerImportPackage BuildMfg(IReadOnlyList<ImportPoint> points)
        {
            if (points.Count == 0) throw new InvalidOperationException("没有要导入的点位。");
            var data = new DesignerXml();
            var features = new List<XElement>(points.Count);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var point in points)
            {
                if (string.IsNullOrWhiteSpace(point.Name)) throw new InvalidOperationException("请填写所有待导入点位的名称。");
                if (!names.Add(point.Name)) throw new InvalidOperationException("待导入点位中存在同名 “" + point.Name + "”，请修改名称。");
                features.Add(data.AddTreeNode("PmWeldPoint", point.Name,
                    new XElement("operatesOn"), new XElement("operatesOnProto"),
                    Vector("location", point.X, point.Y, point.Z), Vector("rotation", 0, 0, 0),
                    new XElement("holdingTime", 0), new XElement("motionTime", 0), new XElement("actionTime", 0)));
            }
            string path = Path.Combine(TxToolsTemp.SessionDirectory("ProcessDesigner", "import"), "TxTools-Mfg.xml");
            return new DesignerImportPackage("MFG 焊点", DesignerTree.Mfg, data.Save(path, features));
        }
    }

    internal sealed class DesignerCadPackage : IDisposable
    {
        private readonly DesignerTree _tree;
        private readonly string _systemRoot, _outputFolder;
        private readonly IReadOnlyDictionary<string, string> _folders;
        private readonly DesignerXml _libraryXml = new DesignerXml(), _structureXml = new DesignerXml();
        private readonly Dictionary<string, XElement> _prototypes = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _instancePrototypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _warnings = new List<string>();
        private readonly XElement _root, _rootLayout;
        private static readonly double[] Zero = { 0, 0, 0 };
        private readonly HashSet<string> _reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _packages = new List<string>();
        private int _missingGeometry;
        private bool _saved;

        private DesignerCadPackage(DesignerTree tree, string systemRoot, string outputFolder, string name,
            IReadOnlyDictionary<string, string> folders)
        {
            _tree = tree;
            _systemRoot = systemRoot;
            _outputFolder = outputFolder;
            _folders = folders;
            if (outputFolder != null) LibraryInsertion.CheckDestination(Path.Combine(outputFolder, "model.cojt"), systemRoot, allowRoot: true);
            (_root, _rootLayout) = AddCompound(StructureEngine.Clean(name), null);
        }

        private bool IsPart => _tree == DesignerTree.Part;

        internal static async Task<DesignerImportPackage> BuildParts(CatiaImportSource source, StructureNode draft, string name,
            string systemRoot, string outputFolder, CancellationToken cancel, IProgress<string> progress,
            IReadOnlyDictionary<string, string> folders = null)
        {
            string staging = null;
            using (var batch = new DesignerCadPackage(DesignerTree.Part, systemRoot, outputFolder, name, folders))
            {
                try
                {
                    var conversion = CatiaImportSession.ForJtExport(source, draft);
                    var placements = new Dictionary<string, (double[] Position, double[] Rotation)>();
                    foreach (var instance in conversion.Items.Where(item => !item.Container))
                    {
                        cancel.ThrowIfCancellationRequested();
                        placements.Add(instance.Node.Key, ReadPose(source.Parts[instance.Node.Key].Frame, instance.Name));
                    }
                    if (outputFolder != null && placements.Count != 0)
                    {
                        staging = TxToolsTemp.SessionDirectory("ProcessDesigner", "jt");
                        await conversion.ConvertAsync(cancel, progress, reduceMesh: false, jtOutputDirectory: staging);
                        batch._warnings.AddRange(conversion.Warnings);
                        batch._missingGeometry = conversion.SkippedInstances;
                    }
                    return await Task.Run(() =>
                    {
                        batch.AddPart(draft, batch._root, batch._rootLayout, source, conversion, placements, cancel);
                        return batch.Save(cancel);
                    }, cancel);
                }
                finally { ResourceConversion.DeleteWorkDirectory(staging); }
            }
        }

        internal static DesignerImportPackage BuildResources(IReadOnlyList<(string File, string Name)> models, string name,
            string systemRoot, string outputFolder, CancellationToken cancel, IProgress<string> progress,
            IReadOnlyDictionary<string, string> folders = null, IReadOnlyDictionary<string, ResourcePrototype> types = null)
        {
            if (models.Count == 0) throw new InvalidOperationException("请添加资源模型。");
            using (var batch = new DesignerCadPackage(DesignerTree.Resource, systemRoot, outputFolder, name, folders))
            {
                for (int i = 0; i < models.Count; i++)
                {
                    cancel.ThrowIfCancellationRequested();
                    var model = models[i];
                    var label = StructureEngine.Clean(model.Name);
                    string staging = TxToolsTemp.SessionDirectory("ProcessDesigner", "jt");
                    try
                    {
                        progress.Report("转换资源 " + (i + 1) + "/" + models.Count + " · " + label);
                        string jt = Path.Combine(staging, "model.jt");
                        try
                        {
                            foreach (string warning in ResourceConversion.Convert(model.File, jt, staging, cancel))
                                batch._warnings.Add(label + "：" + warning);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception error)
                        {
                            batch._warnings.Add("未生成 JT · " + label + "：" + error.GetBaseException().Message);
                            batch._missingGeometry++;
                            jt = null;
                        }
                        string type = types != null && types.TryGetValue(model.File, out var selected)
                            ? selected.Type : ResourcePrototype.BaseType;
                        var prototype = batch.AddPrototype(model.File, label, jt, type);
                        batch.AddInstance(label, prototype, (Zero, Zero), batch._root, batch._rootLayout);
                    }
                    finally { ResourceConversion.DeleteWorkDirectory(staging); }
                }
                return batch.Save(cancel);
            }
        }

        private void AddPart(StructureNode node, XElement parent, XElement parentLayout, CatiaImportSource source,
            CatiaImportSession conversion, IReadOnlyDictionary<string, (double[] Position, double[] Rotation)> placements,
            CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            if (!node.Include) return;
            if (node.IsContainer)
            {
                if (!node.SkipLevel)
                {
                    var compound = AddCompound(StructureEngine.Clean(node.Label), parentLayout);
                    AddChild(parent, compound.Node);
                    parent = compound.Node;
                    parentLayout = compound.Layout;
                }
                foreach (var child in node.Children) AddPart(child, parent, parentLayout, source, conversion, placements, cancel);
            }
            else
            {
                var part = source.Parts[node.Key];
                var name = StructureEngine.Clean(node.Label);
                if (!_prototypes.TryGetValue(part.AssetKey, out var prototype))
                    prototype = AddPrototype(part.AssetKey,
                        string.IsNullOrWhiteSpace(node.PartNumber) ? name : node.PartNumber, conversion.JtForPart(node.Key), "PmPartPrototype");
                AddInstance(name, prototype, placements[node.Key], parent, parentLayout);
            }
        }

        private (XElement Node, XElement Layout) AddCompound(string name, XElement parentLayout)
        {
            var compound = _structureXml.AddTreeNode(IsPart ? "PmCompoundPart" : "PmCompoundResource", name,
                new XElement("children"));
            var layout = AddLayout((Zero, Zero), parentLayout);
            compound.Add(DesignerXml.Reference("layout", layout));
            return (compound, layout);
        }

        private XElement AddPrototype(string key, string name, string jt, string type)
        {
            var representation = jt == null ? null : DesignerXml.Reference("threeDRep", AddRepresentation(key, name, jt));
            var prototype = _libraryXml.AddTreeNode(type, name, representation);
            _prototypes.Add(key, prototype);
            return prototype;
        }

        private XElement AddRepresentation(string key, string name, string jt)
        {
            if (string.IsNullOrWhiteSpace(_systemRoot)) throw new IOException("当前 PD 工程未配置系统库。");
            string folder = _folders != null && _folders.TryGetValue(key, out var assigned) ? assigned : _outputFolder;
            string package = CatiaLibraryDestination.Reserve(folder, CatiaLibraryDestination.FolderSegment(name), ".cojt", _reserved);
            LibraryInsertion.CheckDestination(package, _systemRoot, allowRoot: true);
            CatiaLibraryDestination.CreatePackage(package);
            _packages.Add(package);
            string stem = Path.GetFileNameWithoutExtension(package);
            File.Copy(jt, Path.Combine(package, stem + ".jt"), overwrite: false);
            string relative = CojtTransfer.RelativeToRoot(package, _systemRoot);
            var file = _libraryXml.Add("PmReferenceFile", "ReferenceFile", new XElement("fileName", "#\\" + relative));
            return _libraryXml.Add("Pm3DRep", "3DRep", new XElement("type", 0), DesignerXml.Reference("file", file));
        }

        private void AddInstance(string name, XElement prototype, (double[] Position, double[] Rotation) pose,
            XElement parent, XElement parentLayout)
        {
            var instance = _structureXml.AddTreeNode(IsPart ? "PmPartInstance" : "PmToolInstance", name,
                DesignerXml.Reference("prototype", prototype), new XElement("amount", 1));
            _instancePrototypes.Add(DesignerXml.Id(instance), DesignerXml.Id(prototype));
            instance.Add(DesignerXml.Reference("layout", AddLayout(pose, parentLayout)));
            AddChild(parent, instance);
        }

        private static (double[] Position, double[] Rotation) ReadPose(double[] frame, string name)
        {
            var basis = new Frame12(frame);
            // eBOP layout records store position and a rigid RPY rotation, in mm and radians.
            const double tolerance = 0.00001;
            const string invalidPose = "：来源含镜像、缩放或剪切，eBOP 位姿无法表达此矩阵。";
            if (basis.Determinant < 0) throw new InvalidDataException(name + invalidPose);
            for (int i = 0; i < 3; i++)
            for (int j = i; j < 3; j++)
            {
                double product = frame[i * 3] * frame[j * 3] + frame[i * 3 + 1] * frame[j * 3 + 1] + frame[i * 3 + 2] * frame[j * 3 + 2];
                if (Math.Abs(product - (i == j ? 1 : 0)) > tolerance) throw new InvalidDataException(name + invalidPose);
            }
            var transform = new TxTransformation(frame[0], frame[3], frame[6], frame[9],
                frame[1], frame[4], frame[7], frame[10], frame[2], frame[5], frame[8], frame[11]);
            var rpy = transform.RotationRPY_XYZ;
            return (new[] { frame[9], frame[10], frame[11] }, new[] { rpy.X, rpy.Y, rpy.Z });
        }

        private XElement AddLayout((double[] Position, double[] Rotation) pose, XElement parent)
        {
            var layout = _structureXml.Add("PmLayout", "Layout", new XElement("children"),
                parent == null ? new XElement("parent", "NULL") : DesignerXml.Reference("parent", parent),
                DesignerXml.Vector("location", pose.Position), DesignerXml.Vector("rotation", pose.Rotation));
            if (parent != null) parent.Element("children").Add(new XElement("item", DesignerXml.Id(layout)));
            return layout;
        }

        private static void AddChild(XElement parent, XElement child)
        {
            parent.Element("children").Add(new XElement("item", DesignerXml.Id(child)));
            child.Add(DesignerXml.List("parents", new[] { parent }));
        }

        private DesignerImportPackage Save(CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            if (!_root.Element("children").HasElements) throw new InvalidOperationException("没有可导入的结构节点。");
            string folder = TxToolsTemp.SessionDirectory("ProcessDesigner", "import");
            var library = _prototypes.Count == 0 ? null
                : _libraryXml.Save(Path.Combine(folder, "TxTools-" + _tree + "-Library.xml"), _prototypes.Values);
            var structure = _structureXml.Save(Path.Combine(folder, "TxTools-" + _tree + "-Structure.xml"), new[] { _root });
            var result = new DesignerImportPackage((string)_root.Element("name"), _tree, library, structure,
                _instancePrototypes, _warnings.ToArray(), _missingGeometry);
            _saved = true;
            return result;
        }

        public void Dispose()
        {
            if (!_saved)
                foreach (string package in _packages) ResourceConversion.DeleteWorkDirectory(package);
        }
    }
}
