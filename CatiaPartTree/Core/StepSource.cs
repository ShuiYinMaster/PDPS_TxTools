using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using TxTools.CatiaPartTree.Conversion;
using TxTools.Common.TreeWorkflow;

namespace TxTools.CatiaPartTree.Core
{
    internal static class StepSource
    {
        internal static CatiaImportSource Read(string path, CancellationToken cancel = default(CancellationToken), bool readVisibility = false)
        {
            path = Path.GetFullPath(path);
            string extension = Path.GetExtension(path);
            if (!extension.Equals(".stp", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".step", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("请选择 .stp 或 .step 文件。");

            string staging = CreateStaging();
            try
            {
                using (var stream = File.OpenRead(path))
                {
                    string tree = Path.Combine(staging, "tree.xml");
                    RunReader(cancel, readVisibility ? "visible-tree" : "tree", path, tree);
                    return ReadTree(tree, path, CatiaImportSource.Hash(stream));
                }
            }
            finally { Directory.Delete(staging, true); }
        }

        private static CatiaImportSource ReadTree(string tree, string path, string hash)
        {
            XElement assembly;
            using (var xml = XmlReader.Create(tree, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                assembly = XDocument.Load(xml).Root;
            if (assembly?.Name != "step" || (string)assembly.Attribute("version") != "1")
                throw new InvalidDataException("STEP 产品树头无效。");
            var roots = assembly.Elements().ToArray();
            if (roots.Length == 0) throw new InvalidDataException("STEP 文件没有可读取的产品。");
            var source = new CatiaImportSource { DocumentPath = path };
            int count = 0;
            StructureNode ReadNode(XElement element, string key, Frame12 parent, int depth, bool parentHidden)
            {
                if (depth > 100 || ++count > 100000) throw new InvalidDataException("STEP 装配层级或实例数超限。");
                if (element.Name != "assembly" && element.Name != "part")
                    throw new InvalidDataException("STEP 产品节点类型无效。");
                string name = (string)element.Attribute("name");
                string definitionName = (string)element.Attribute("definitionName");
                string matrix = (string)element.Attribute("frame") ?? "";
                var frame = parent.ThenChild(new Frame12(matrix.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray()));
                bool hidden = parentHidden || (string)element.Attribute("hidden") == "1";
                var node = new StructureNode { Key = key, SourceName = string.IsNullOrWhiteSpace(name) ? definitionName : name,
                    PartNumber = definitionName, IsContainer = element.Name == "assembly" };
                if (node.IsContainer)
                {
                    int index = 0;
                    foreach (var child in element.Elements())
                        node.Add(ReadNode(child, key + "/" + ++index, frame, depth + 1, hidden));
                }
                else
                {
                    string id = (string)element.Attribute("definition");
                    if (string.IsNullOrEmpty(id) || id.Length > 100 || id.Any(c => (c < '0' || c > '9') && c != '_') || element.HasElements)
                        throw new InvalidDataException("STEP 零件引用无效。");
                    source.Parts.Add(key, new CatiaSourcePart { File = path, ReferenceId = id, StepDefinition = id, InstancePath = key,
                        Frame = frame.Components, Hidden = hidden,
                        ConversionIssue = (string)element.Attribute("mesh") == "1" ? null : "STEP 零件没有可细分的面。" });
                }
                return node;
            }
            if (roots.Length == 1)
                source.Root = ReadNode(roots[0], "step/1", Frame12.Identity, 0, false);
            else
            {
                source.Root = new StructureNode { Key = "step", SourceName = Path.GetFileNameWithoutExtension(path), IsContainer = true };
                for (int i = 0; i < roots.Length; i++)
                    source.Root.Add(ReadNode(roots[i], "step/" + (i + 1), Frame12.Identity, 1, false));
            }
            source.Files.Add(path, hash);
            return source;
        }

        internal static string CreateStaging()
        {
            string path = Path.Combine(Path.GetTempPath(), "TxTools-STEP-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        internal static void PrepareMeshes(string source, IEnumerable<string> definitions, string staging, CancellationToken cancel,
            bool skipHidden = false)
        {
            string list = Path.Combine(staging, "parts.txt");
            File.WriteAllLines(list, definitions, Encoding.ASCII);
            RunReader(cancel, skipHidden ? "visible-mesh" : "mesh", source, list, staging);
        }

        internal static string MeshIssue(string meshPath)
        {
            if (File.Exists(meshPath)) return null;
            string diagnostic = Path.ChangeExtension(meshPath, ".error");
            string issue = File.Exists(diagnostic) ? File.ReadAllText(diagnostic, Encoding.UTF8).Trim() : null;
            return string.IsNullOrEmpty(issue) ? "STEP 读取器未生成零件网格。" : issue;
        }

        internal static string MeshCoverageWarning(string meshPath)
        {
            string diagnostic = Path.ChangeExtension(meshPath, ".warning");
            if (!File.Exists(diagnostic)) return null;
            var counts = File.ReadAllText(diagnostic, Encoding.UTF8).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (counts.Length != 2 ||
                !int.TryParse(counts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int written) || written <= 0 ||
                !int.TryParse(counts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int missing) || missing <= 0)
                return "已生成 STEP 网格，但无法读取缺面数量。";
            return "已保留 " + written + " 个面，" + missing + " 个面未生成网格，模型可能有开口。";
        }

        private static void RunReader(CancellationToken cancel, params string[] arguments)
        {
            string reader = Path.Combine(Path.GetDirectoryName(typeof(StepSource).Assembly.Location), "CatiaPartTree", "Step", "TxTools.StepReader.exe");
            if (!File.Exists(reader)) throw new FileNotFoundException("缺少 STEP 读取器。请完整部署 CatiaPartTree/Step 目录。", reader);
            cancel.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(reader, string.Join(" ", arguments.Select(value => "\"" + value + "\"")))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(reader)
            };
            using (var process = Process.Start(start))
            {
                var error = process.StandardError.ReadToEndAsync();
                var output = process.StandardOutput.ReadToEndAsync();
                try
                {
                    while (!process.WaitForExit(200)) cancel.ThrowIfCancellationRequested();
                    Task.WaitAll(error, output);
                    if (process.ExitCode != 0)
                        throw new IOException("STEP 读取失败（退出码 " + process.ExitCode + "）：" + error.Result.Trim());
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
    }
}
