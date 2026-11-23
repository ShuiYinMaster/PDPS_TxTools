using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Tecnomatix.Engineering;
using Tecnomatix.Engineering.Utilities;

namespace TxTools.ResourceImporter
{
    internal sealed class ResourcePrototype
    {
        internal const string Family = "ToolPrototype";
        private const string BaseType = "PmToolPrototype";
        internal readonly string Type, Label;

        private ResourcePrototype(string type, string label)
        {
            Type = type;
            Label = string.IsNullOrWhiteSpace(label) ? type : label;
        }

        internal static ResourcePrototype[] Read(TxDocument document)
        {
            var types = ReadActiveTypes(document.PlatformGlobalServicesProvider) ?? ReadStudyTypes(document.FinalDestination);
            if (types == null)
            {
                var path = ResourceCustomizationInstallation.FindDefault();
                if (path == null)
                    throw new FileNotFoundException("当前工程没有可读取的原型类型，且未找到当前 PS 版本的 defaultCust.ppc。");
                types = ReadPpcTypes(path);
            }

            if (types.Length == 0) throw new InvalidDataException("当前客制化未定义可用于导入的 ToolPrototype 类型。");
            return types;
        }

        private static ResourcePrototype[] ReadActiveTypes(ITxPlatformGlobalServicesProvider provider)
        {
            TxPlanningTypeMetaData root;
            TxPlanningTypeMetaData[] descendants;
            try
            {
                if (!provider.IsPlanningTypeMetaDataInitialized()) return null;
                root = provider.GetTypeMetaData(BaseType);
                if (root == null) return null;
                try
                {
                    descendants = root.DerivedTypes?.OfType<TxPlanningTypeMetaData>().ToArray()
                        ?? Array.Empty<TxPlanningTypeMetaData>();
                }
                catch (TxPlanningObjectDerivedPlanningTypeDoesNotExistException)
                {
                    descendants = Array.Empty<TxPlanningTypeMetaData>();
                }
            }
            catch (TxException) { return null; }

            var manager = new TxTunePmDocumentManager();
            var types = new List<ResourcePrototype>();
            foreach (var metadata in new[] { root }.Concat(descendants))
            {
                string name = ClassName(metadata.TypeName);
                if (string.IsNullOrEmpty(name)) continue;
                try
                {
                    var prototype = manager.CreatePrototypeData(name, 0);
                    if (prototype != null && string.Equals(prototype.Family, Family, StringComparison.OrdinalIgnoreCase))
                        types.Add(new ResourcePrototype(prototype.Type, metadata.DisplayName));
                }
                catch (TxPlanningTypeDoesNotExistException) { }
            }
            return types.Count == 0 ? null : Sort(types);
        }

        internal static ResourcePrototype[] ReadStudyTypes(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            string extension = Path.GetExtension(path);
            if (!extension.Equals(".psz", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".pszt", StringComparison.OrdinalIgnoreCase)) return null;
            using (var archive = ZipFile.OpenRead(path))
            {
                var entry = archive.GetEntry("metadata.xml");
                if (entry == null) return null;
                using (var stream = entry.Open())
                using (var reader = XmlReader.Create(stream, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 * 1024
                }))
                {
                    var root = XDocument.Load(reader).Root;
                    var classes = Child(Child(root, "ClassTypeData"), "Classes");
                    if (classes == null) return null;
                    var types = classes.Elements().Where(item => item.Name.LocalName == "Item")
                        .ToDictionary(item => (string)Child(item, "Key"), StringComparer.Ordinal);
                    var baseType = types.FirstOrDefault(pair => ClassName((string)Child(pair.Value, "Name")) == BaseType);
                    if (baseType.Key == null) return Array.Empty<ResourcePrototype>();
                    var labels = types.ToDictionary(pair => pair.Key, pair => new ResourcePrototype(
                        ClassName((string)Child(pair.Value, "Name")), (string)Child(pair.Value, "NiceName")), StringComparer.Ordinal);
                    // Follow the stored class keys, including transitive custom types.
                    return CollectTools(baseType.Key, labels, key =>
                        Child(types[key], "Successors")?.Elements().Where(node => node.Name.LocalName == "SuccessorKey")
                            .Select(node => node.Value) ?? Enumerable.Empty<string>(), true);
                }
            }
        }

        private static XElement Child(XElement parent, string name) =>
            parent?.Elements().FirstOrDefault(element => element.Name.LocalName == name);

        internal static ResourcePrototype[] ReadPpcTypes(string path)
        {
            string content = File.ReadAllText(path, Encoding.Default);
            var block = Regex.Match(content, @"(?m)^\s*SOFT_CLASSES\s*\{(?<classes>[^}]*)\}");
            if (!block.Success) throw new InvalidDataException("找不到 PPC 的 SOFT_CLASSES 段：" + path);
            string definitions = Regex.Replace(block.Groups["classes"].Value, @"/\*[\s\S]*?\*/|//[^\r\n]*", "");
            var children = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var types = new Dictionary<string, ResourcePrototype>(StringComparer.OrdinalIgnoreCase)
            {
                [BaseType] = new ResourcePrototype(BaseType, Family)
            };
            foreach (Match item in Regex.Matches(definitions, @"'class\s+([^']+)'\s*:\s*'class\s+([^']+)'"))
            {
                string name = item.Groups[1].Value.Trim(), parent = item.Groups[2].Value.Trim();
                types[name] = new ResourcePrototype(name, name);
                if (!children.TryGetValue(parent, out var list)) children.Add(parent, list = new List<string>());
                list.Add(name);
            }
            return CollectTools(BaseType, types, key => children.TryGetValue(key, out var list)
                ? (IEnumerable<string>)list : Enumerable.Empty<string>(), false);
        }

        private static ResourcePrototype[] CollectTools(string root, Dictionary<string, ResourcePrototype> types,
            Func<string, IEnumerable<string>> children, bool includeBase)
        {
            var pending = new Stack<string>();
            var visited = new HashSet<string>(types.Comparer);
            var result = new List<ResourcePrototype>();
            pending.Push(root);
            while (pending.Count != 0)
            {
                string key = pending.Pop();
                if (!visited.Add(key) || !types.TryGetValue(key, out var type) || string.IsNullOrEmpty(type.Type)) continue;
                // PS can store EquipmentPrototype under PmToolPrototype in the class graph.
                if (type.Type == "PmEquipmentPrototype" || type.Type == "PmPartPrototype") continue;
                if (includeBase || key != root) result.Add(type);
                foreach (string child in children(key)) pending.Push(child);
            }
            return Sort(result);
        }

        private static string ClassName(string name)
        {
            name = name?.Trim();
            return name != null && name.StartsWith("class ", StringComparison.Ordinal) ? name.Substring(6).Trim() : name;
        }

        private static ResourcePrototype[] Sort(IEnumerable<ResourcePrototype> types) =>
            types.Where(type => !string.IsNullOrWhiteSpace(type.Type))
                .GroupBy(type => type.Type, StringComparer.OrdinalIgnoreCase).Select(group => group.First())
                .OrderBy(type => type.Label, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
}
