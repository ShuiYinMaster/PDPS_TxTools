using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TxTools.Agent.Core;
using TxTools.CatiaPartTree.Core;
using TxTools.Common.TreeWorkflow;

namespace TxTools.ResourceImporter
{
    internal static class ResourceConversion
    {
        internal const string LibraryStagePrefix = ".tx-ri-";
        private const string Patterns = "*.jt;*.step;*.stp;*.3dxml;*.cgr;*.catpart;*.catproduct";
        internal const string FileFilter = "支持的模型文件|" + Patterns +
            "|JT|*.jt|STEP|*.step;*.stp|3DXML|*.3dxml|CGR|*.cgr|CATPart / CATProduct|*.catpart;*.catproduct";

        internal static bool Supports(string path)
        {
            return Patterns.Split(';').Any(pattern => string.Equals(pattern.Substring(1),
                Path.GetExtension(path), StringComparison.OrdinalIgnoreCase));
        }

        internal static (string Package, string[] Warnings) PreparePackage(string source, string finalPackage, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            source = Path.GetFullPath(source);
            finalPackage = Path.GetFullPath(finalPackage);
            string workspace = null, stage = null;
            try
            {
                string converted = source;
                var warnings = Array.Empty<string>();
                if (!Path.GetExtension(source).Equals(".jt", StringComparison.OrdinalIgnoreCase))
                {
                    workspace = TxToolsTemp.SessionDirectory("ResourceImporter", "jt");
                    converted = Path.Combine(workspace, "model.jt");
                    warnings = Convert(source, converted, workspace, cancel);
                }
                cancel.ThrowIfCancellationRequested();

                // Keep the complete package on the target volume for Directory.Move.
                string reservation = Path.Combine(Path.GetDirectoryName(finalPackage), LibraryStagePrefix + Path.GetRandomFileName());
                CatiaLibraryDestination.CreatePackage(reservation);
                stage = reservation;
                string package = Path.Combine(stage, Path.GetFileName(finalPackage));
                Directory.CreateDirectory(package);
                string jt = Path.Combine(package, Path.GetFileNameWithoutExtension(finalPackage) + ".jt");
                using (var input = File.OpenRead(converted))
                    CopyJt(input, jt, cancel);
                cancel.ThrowIfCancellationRequested();
                stage = null; // The caller owns the staging directory after success.
                return (package, warnings);
            }
            finally
            {
                DeleteWorkDirectory(stage);
                DeleteWorkDirectory(workspace);
            }
        }

        internal static void DeleteWorkDirectory(string directory)
        {
            if (directory == null) return;
            try { Directory.Delete(directory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal static string[] Convert(string source, string jt, string workspace, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            source = Path.GetFullPath(source);
            jt = Path.GetFullPath(jt);
            workspace = Path.GetFullPath(workspace);
            string extension = Path.GetExtension(source).ToLowerInvariant();
            var warnings = new List<string>();
            using (var input = File.OpenRead(source))
            {
                if (extension == ".jt")
                {
                    CopyJt(input, jt, cancel);
                    return Array.Empty<string>();
                }
                string digest, option = null;
                switch (extension)
                {
                    case ".step":
                    case ".stp":
                    case ".3dxml":
                        option = "assembly=" + PrepareAssembly(source, extension == ".3dxml", workspace, cancel, warnings, out digest);
                        break;
                    case ".cgr":
                        digest = CatiaImportSource.Hash(input);
                        option = "cgr";
                        break;
                    case ".catpart":
                        digest = CatiaImportSource.Hash(input);
                        break;
                    case ".catproduct":
                        string index = source + ".jtcoords.json";
                        if (!File.Exists(index))
                            throw new FileNotFoundException("CATProduct 缺少同名离线装配索引：" + Path.GetFileName(index), index);
                        digest = CatiaImportSource.Hash(input);
                        option = "assembly=" + CatProductConversion.WriteIndexedAssembly(source, index, digest, workspace, cancel);
                        break;
                    default:
                        throw new NotSupportedException("不支持的模型格式：" + extension);
                }
                var arguments = new List<string> { source, jt, digest };
                if (option != null) arguments.Add(option);
                string worker = Path.Combine(Path.GetDirectoryName(typeof(ResourceConversion).Assembly.Location),
                    "CatiaPartTree", "TxTools.CatiaConverter.exe");
                if (!File.Exists(worker)) throw new FileNotFoundException("缺少 JT 转换程序，请完整部署插件运行目录。", worker);
                warnings.AddRange(RunConverter(worker, arguments, workspace, cancel, warnings));
            }
            if (!File.Exists(jt) || new FileInfo(jt).Length < 105)
                throw new IOException("转换程序未生成有效的 JT。");
            return warnings.ToArray();
        }

        private static void CopyJt(Stream input, string output, CancellationToken cancel)
        {
            var header = new byte[8];
            if (input.Length < 105 || input.Read(header, 0, header.Length) != header.Length ||
                Encoding.ASCII.GetString(header) != "Version ")
                throw new InvalidDataException("JT 文件头无效。");
            input.Position = 0;
            using (var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write))
            {
                var buffer = new byte[1024 * 1024];
                int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    target.Write(buffer, 0, count);
                }
            }
        }

        private static string PrepareAssembly(string file, bool threeDXml, string workspace, CancellationToken cancel,
            List<string> warnings, out string digest)
        {
            var source = threeDXml ? ThreeDXmlSource.Read(file, cancel, allowXmlGeometry: true) : StepSource.Read(file, cancel);
            digest = source.Files[file];
            var nodes = new Dictionary<string, StructureNode>();
            IndexNodes(source.Root, nodes);
            var definitions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (threeDXml)
            {
                int representationIndex = 0;
                using (var archive = ZipFile.OpenRead(file))
                {
                    foreach (var group in source.Parts.GroupBy(pair => pair.Value.GeometryEntry, StringComparer.OrdinalIgnoreCase))
                    {
                        cancel.ThrowIfCancellationRequested();
                        string issue = group.First().Value.ConversionIssue;
                        if (issue == null)
                        {
                            string extension = ThreeDXmlSource.IsXmlRepresentation(group.Key) ? ".3drep" : ".cgr";
                            string extracted = Path.Combine(workspace, representationIndex++ + extension);
                            try
                            {
                                ThreeDXmlSource.ExtractGeometry(archive, group.Key, extracted, cancel);
                                definitions.Add(group.Key, extracted);
                            }
                            catch (InvalidDataException error) { issue = error.Message; }
                        }
                        if (issue != null)
                            warnings.Add("已跳过 " + InstanceName(nodes[group.First().Key]) + "（" + group.Count() + " 个实例）：" + issue);
                    }
                }
            }
            else
            {
                var groups = source.Parts.GroupBy(pair => pair.Value.StepDefinition).ToArray();
                var meshable = groups.Where(group => group.First().Value.ConversionIssue == null).Select(group => group.Key).ToArray();
                if (meshable.Length != 0) StepSource.PrepareMeshes(file, meshable, workspace, cancel);
                foreach (var group in groups)
                {
                    cancel.ThrowIfCancellationRequested();
                    var first = group.First();
                    string mesh = Path.Combine(workspace, group.Key + ".mesh");
                    string issue = first.Value.ConversionIssue ?? StepSource.MeshIssue(mesh);
                    var node = nodes[first.Key];
                    string label = string.IsNullOrWhiteSpace(node.PartNumber) ? InstanceName(node) : node.PartNumber;
                    if (issue == null)
                    {
                        definitions.Add(group.Key, mesh);
                        string coverage = StepSource.MeshCoverageWarning(mesh);
                        if (coverage != null) warnings.Add(label + "（" + group.Count() + " 个实例）：" + coverage);
                    }
                    else
                        warnings.Add("已跳过 " + label + "（" + group.Count() + " 个实例）：" + issue);
                }
            }
            string GeometryKey(CatiaSourcePart part) => threeDXml ? part.GeometryEntry : part.StepDefinition;
            var parts = source.Parts.Where(pair => definitions.ContainsKey(GeometryKey(pair.Value))).ToArray();
            if (parts.Length == 0)
                throw new InvalidDataException(string.Join(Environment.NewLine,
                    new[] { "装配没有可导入的面几何。" }.Concat(warnings)));
            string manifest = Path.Combine(workspace, "assembly.bin");
            using (var writer = new BinaryWriter(File.Create(manifest)))
            {
                writer.Write(parts.Length);
                foreach (var pair in parts)
                {
                    cancel.ThrowIfCancellationRequested();
                    var part = pair.Value;
                    writer.Write(definitions[GeometryKey(part)]);
                    writer.Write(InstanceName(nodes[pair.Key]));
                    foreach (double value in part.Frame) writer.Write(value);
                }
            }
            return manifest;
        }

        private static string InstanceName(StructureNode node) => string.IsNullOrWhiteSpace(node.SourceName) ? node.Key : node.SourceName;

        private static void IndexNodes(StructureNode node, IDictionary<string, StructureNode> nodes)
        {
            nodes.Add(node.Key, node);
            foreach (var child in node.Children) IndexNodes(child, nodes);
        }

        private static string[] RunConverter(string executable, IEnumerable<string> arguments, string workspace, CancellationToken cancel,
            IReadOnlyCollection<string> preparationWarnings)
        {
            cancel.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(executable, string.Join(" ", arguments.Select(QuoteArgument)))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
                RedirectStandardOutput = true, StandardErrorEncoding = Encoding.UTF8, StandardOutputEncoding = Encoding.UTF8,
                WorkingDirectory = workspace
            };
            using (var process = Process.Start(start))
            {
                var error = process.StandardError.ReadToEndAsync();
                var output = process.StandardOutput.ReadToEndAsync();
                try
                {
                    while (!process.WaitForExit(200)) cancel.ThrowIfCancellationRequested();
                    Task.WaitAll(error, output);
                    cancel.ThrowIfCancellationRequested();
                    var warnings = output.Result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Where(line => line.StartsWith("STEP-WARNING ", StringComparison.Ordinal) ||
                            line.StartsWith("3DXML-WARNING ", StringComparison.Ordinal))
                        .Select(line => line.Substring(line.IndexOf(' ') + 1)).ToArray();
                    if (process.ExitCode != 0)
                    {
                        string detail = error.Result.Trim();
                        if (detail.Length == 0) detail = output.Result.Trim();
                        string failure = "JT 转换失败（退出码 " + process.ExitCode + "）";
                        throw new IOException(string.Join(Environment.NewLine,
                            new[] { detail.Length == 0 ? failure : failure + "：" + detail }.Concat(preparationWarnings).Concat(warnings)));
                    }
                    return warnings;
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        try { process.Kill(); }
                        catch (InvalidOperationException) when (process.HasExited) { }
                        process.WaitForExit();
                    }
                }
            }
        }

        private static string QuoteArgument(string value)
        {
            if (value.IndexOf('"') >= 0) throw new IOException("转换路径包含无效引号。");
            int trailing = value.Length - value.TrimEnd('\\').Length;
            return "\"" + value + new string('\\', trailing) + "\"";
        }
    }
}
