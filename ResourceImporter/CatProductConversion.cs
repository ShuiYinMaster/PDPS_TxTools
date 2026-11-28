using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using TxTools.CatiaPartTree.Conversion;
using TxTools.CatiaPartTree.Core;

namespace TxTools.ResourceImporter
{
    internal static class CatProductConversion
    {
        private sealed class AssemblyIndex
        {
            [JsonProperty("schema")] public string Schema { get; set; }
            [JsonProperty("unit")] public string Unit { get; set; }
            [JsonProperty("assembly_sha256")] public string Digest { get; set; }
            [JsonProperty("root_position_components")] public double[] RootPosition { get; set; }
            [JsonProperty("nodes")] public IndexNode[] Nodes { get; set; }
        }

        private sealed class IndexNode
        {
            [JsonProperty("id")] public string Id { get; set; }
            [JsonProperty("parent_id")] public string Parent { get; set; }
            [JsonProperty("instance_name")] public string Name { get; set; }
            [JsonProperty("source_relative_path")] public string Path { get; set; }
            [JsonProperty("source_sha256")] public string Digest { get; set; }
            [JsonProperty("is_part", Required = Required.Always)] public bool IsPart { get; set; }
            [JsonProperty("relative_components")] public double[] Local { get; set; }
            [JsonProperty("root_components")] public double[] World { get; set; }
        }

        internal static string WriteIndexedAssembly(string source, string indexPath, string digest, string workspace, CancellationToken cancel)
        {
            if (new FileInfo(indexPath).Length > 32 * 1024 * 1024) throw new InvalidDataException("CATProduct 装配索引超过大小上限。");
            AssemblyIndex index;
            using (var reader = new JsonTextReader(File.OpenText(indexPath)) { MaxDepth = 16 })
                index = new JsonSerializer().Deserialize<AssemblyIndex>(reader);
            if (index?.Schema != "cad-drop.offline-assembly.v1" || index.Unit != "mm" ||
                index.Nodes == null || index.Nodes.Length == 0 || index.Nodes.Length > 100000)
                throw new InvalidDataException("CATProduct 装配索引格式无效。");
            if (!string.Equals(index.Digest, digest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("CATProduct 已变化，装配索引的文件摘要不匹配。");
            if (!SameFrame(new Frame12(index.RootPosition), Frame12.Identity))
                throw new InvalidDataException("CATProduct 装配索引的根坐标系无效。");

            string directory = Path.GetDirectoryName(source);
            string prefix = directory.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var frames = new Dictionary<string, (Frame12 Frame, bool IsPart)>(StringComparer.Ordinal);
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var parts = new List<(string Path, string Name, string Digest, Frame12 Frame)>();
            var structure = new AssemblyStructureNode { Name = Path.GetFileNameWithoutExtension(source) };
            var structureNodes = new Dictionary<string, AssemblyStructureNode>(StringComparer.Ordinal);
            foreach (var node in index.Nodes)
            {
                cancel.ThrowIfCancellationRequested();
                if (node == null || string.IsNullOrWhiteSpace(node.Id) || string.IsNullOrWhiteSpace(node.Path) ||
                    Path.IsPathRooted(node.Path) || !Regex.IsMatch(node.Digest ?? "", "\\A[0-9a-fA-F]{64}\\z"))
                    throw new InvalidDataException("CATProduct 装配索引包含无效引用。");
                string file = Path.GetFullPath(Path.Combine(directory, node.Path));
                string extension = node.IsPart ? ".CATPart" : ".CATProduct";
                if (!file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetExtension(file).Equals(extension, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("CATProduct 引用须位于所选装配目录中：" + node.Path);
                CrossEnvIO.TransferPaths.CheckParents(file);
                var local = new Frame12(node.Local);
                Frame12 frame;
                if (frames.Count == 0)
                {
                    if (node.Id != "root" || !string.IsNullOrEmpty(node.Parent) || node.IsPart ||
                        !ResourceImportSession.SamePath(file, source) || !SameFrame(local, Frame12.Identity))
                        throw new InvalidDataException("CATProduct 装配索引与所选根文件不匹配。");
                    frame = Frame12.Identity;
                    structureNodes.Add(node.Id, structure);
                }
                else
                {
                    if (string.IsNullOrEmpty(node.Parent) || !node.Id.StartsWith(node.Parent + "/", StringComparison.Ordinal) ||
                        !frames.TryGetValue(node.Parent, out var parent) || parent.IsPart ||
                        !structureNodes.TryGetValue(node.Parent, out var parentNode))
                        throw new InvalidDataException("CATProduct 装配索引的父实例无效：" + node.Id);
                    frame = parent.Frame.ThenChild(local);
                    string instanceName = string.IsNullOrWhiteSpace(node.Name)
                        ? Path.GetFileNameWithoutExtension(file) : node.Name;
                    if (node.IsPart)
                    {
                        var part = new AssemblyStructureNode { Name = instanceName, IsPart = true, PartIndex = parts.Count };
                        parts.Add((file, instanceName, node.Digest.ToLowerInvariant(), frame));
                        parentNode.Add(part);
                    }
                    else parentNode.Add(new AssemblyStructureNode { Name = instanceName });
                    structureNodes.Add(node.Id, parentNode.Children[parentNode.Children.Count - 1]);
                }
                if (!SameFrame(frame, new Frame12(node.World)) || frames.ContainsKey(node.Id))
                    throw new InvalidDataException("CATProduct 装配索引的实例或变换无效：" + node.Id);
                frames.Add(node.Id, (frame, node.IsPart));
                if (files.TryGetValue(file, out string previous))
                {
                    if (!string.Equals(previous, node.Digest, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("CATProduct 引用摘要冲突：" + node.Path);
                }
                else
                {
                    if (!File.Exists(file)) throw new FileNotFoundException("CATProduct 缺少引用文件：" + node.Path, file);
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("CATProduct 引用须使用实际文件：" + node.Path);
                    if (!node.IsPart)
                    {
                        string actualDigest = ResourceImportSession.SamePath(file, source) ? digest : CatiaImportSource.Hash(file);
                        if (!string.Equals(actualDigest, node.Digest, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("CATProduct 引用与装配索引不一致：" + node.Path);
                    }
                    files.Add(file, node.Digest);
                }
            }
            if (parts.Count == 0) throw new InvalidDataException("CATProduct 装配索引没有可导入的 CATPart 实例。");
            string manifest = Path.Combine(workspace, "assembly.bin");
            using (var stream = File.Create(manifest))
                AssemblyManifest.Write(stream, parts.Select(part => new AssemblyPartRecord
                {
                    GeometryPath = part.Path,
                    Name = part.Name,
                    Frame = part.Frame.Components,
                    Digest = part.Digest
                }).ToList(), structure, true);
            return manifest;
        }

        private static bool SameFrame(Frame12 first, Frame12 second) =>
            first.Components.Zip(second.Components, (a, b) => Math.Abs(a - b)).All(delta => delta < 1e-8);

    }
}
