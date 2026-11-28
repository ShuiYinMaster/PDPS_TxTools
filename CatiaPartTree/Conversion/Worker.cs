using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace TxTools.CatiaPartTree.Conversion
{
    internal static class SourceDigest
    {
        internal static string Hash(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
    }

    // Geometry parsing never runs inside the Process Simulate process.
    internal static class Worker
    {
        // 诊断：把解析后的源网格原样导出（顶点顺序即缓存顺序），供离线比对使用。
        // 格式：int32 形状数，然后每个形状 int32 顶点数、int32 三角形数、
        // float32[3*顶点] 坐标、int32[3*三角形] 索引。
        internal static void DumpMesh(SourceMeshScene scene, string path)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(scene.shapes.Length);
                foreach (var shape in scene.shapes)
                {
                    writer.Write(shape.vertices.Length);
                    writer.Write(shape.triangles.Length);
                    foreach (var vertex in shape.vertices)
                    {
                        writer.Write((float)vertex[0]); writer.Write((float)vertex[1]); writer.Write((float)vertex[2]);
                    }
                    foreach (var triangle in shape.triangles)
                    {
                        writer.Write(triangle[0]); writer.Write(triangle[1]); writer.Write(triangle[2]);
                    }
                }
            }
            Console.WriteLine("dumpmesh shapes=" + scene.shapes.Length + " vertices=" +
                scene.shapes.Sum(s => s.vertices.Length) + " triangles=" + scene.shapes.Sum(s => s.triangles.Length) +
                " path=" + path);
        }
        // 诊断：把离线重建（或任何外部工具产出）的 OBJ 直接写成 JT，
        // 用于评估"不经过减面"的路径。法向按面积加权平滑重建。
        internal static SourceMeshScene ObjScene(string path, string sourceFile, string sourceSha256)
        {
            var points = new System.Collections.Generic.List<double[]>();
            var faces = new System.Collections.Generic.List<int[]>();
            foreach (string raw in File.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.Length < 2) continue;
                if (line[0] == 'v' && line[1] == ' ')
                {
                    string[] part = line.Substring(2).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (part.Length < 3) throw new InvalidDataException("OBJ 顶点格式错误：" + raw);
                    points.Add(new[] { double.Parse(part[0], CultureInfo.InvariantCulture),
                        double.Parse(part[1], CultureInfo.InvariantCulture),
                        double.Parse(part[2], CultureInfo.InvariantCulture) });
                }
                else if (line[0] == 'f' && line[1] == ' ')
                {
                    string[] part = line.Substring(2).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (part.Length < 3) continue;
                    var index = new int[3];
                    for (int k = 0; k < 3; k++)
                    {
                        string token = part[k].Split('/')[0];
                        index[k] = int.Parse(token, CultureInfo.InvariantCulture) - 1;
                        if (index[k] < 0) throw new InvalidDataException("OBJ 索引错误：" + raw);
                    }
                    faces.Add(index);
                }
            }
            if (faces.Count == 0) throw new InvalidDataException("OBJ 里没有三角形：" + path);

            var accumulated = new double[points.Count][];
            for (int i = 0; i < accumulated.Length; i++) accumulated[i] = new double[3];
            foreach (var f in faces)
            {
                if (f[0] >= points.Count || f[1] >= points.Count || f[2] >= points.Count)
                    throw new InvalidDataException("OBJ 索引越界：" + f[0] + "," + f[1] + "," + f[2]);
                double[] a = points[f[0]], b = points[f[1]], c = points[f[2]];
                double nx = (b[1] - a[1]) * (c[2] - a[2]) - (b[2] - a[2]) * (c[1] - a[1]);
                double ny = (b[2] - a[2]) * (c[0] - a[0]) - (b[0] - a[0]) * (c[2] - a[2]);
                double nz = (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]);
                foreach (int v in f)
                {
                    accumulated[v][0] += nx; accumulated[v][1] += ny; accumulated[v][2] += nz;
                }
            }
            var normals = new double[points.Count][];
            for (int i = 0; i < points.Count; i++)
            {
                double x = accumulated[i][0], y = accumulated[i][1], z = accumulated[i][2];
                double length = Math.Sqrt(x * x + y * y + z * z);
                normals[i] = length == 0 ? new[] { 0.0, 0.0, 1.0 } : new[] { x / length, y / length, z / length };
            }
            var color = new[] { (byte)190, (byte)190, (byte)190 };
            var mesh = new MeshShape
            {
                name = Path.GetFileNameWithoutExtension(path),
                instanceId = "source/1",
                sourceFile = sourceFile,
                sourceSha256 = sourceSha256,
                vertices = points.ToArray(),
                triangles = faces.ToArray(),
                normals = normals,
                rgb = faces.Select(_ => color).ToArray()
            };
            mesh.Validate();
            Console.WriteLine("obj triangles=" + mesh.triangles.Length + " vertices=" + mesh.vertices.Length);
            return new SourceMeshScene
            {
                name = mesh.name, sourceFile = sourceFile, sourceSha256 = sourceSha256,
                shapes = new[] { mesh }
            };
        }

            // 体积目标：不要求用户填三角形数，按实际写出的文件大小迭代三角形预算，
            // 取"不超过目标体积"的最大密度。三角形数与体积近似线性（链式条带约 18 字节/三角形）。
        private static int TuneToSize(SourceMeshScene scene, string output, int targetKb)
        {
            var original = scene.shapes;
            int total = original.Sum(s => s.triangles.Length);
            long budget = targetKb * 1024L;
            int current = (int)Math.Min(total, Math.Max(1000L, budget / 18L));
            int best = 0, previous = int.MaxValue;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (File.Exists(output)) File.Delete(output);
                scene.shapes = SimplifyAll(original, current);
                Jt8Writer.Write(scene, output);
                long size = new FileInfo(output).Length;
                Console.WriteLine("targetkb=" + targetKb + " try=" + current + " size=" + (size / 1024) + "KB");
                if (size <= budget)
                {
                    best = current;
                    if (size >= budget * 0.85) break;                     // 已经贴近目标，够用
                    long raised = (long)(current * (double)budget / Math.Max(size, 1) * 0.98);
                    current = (int)Math.Min(total, Math.Max(current + 500L, raised));
                    continue;
                }
                int achieved = scene.shapes.Sum(s => s.triangles.Length);
                if (achieved >= previous) break;                              // 已经减不动，别空转
                previous = achieved;
                long reduced = (long)(current * (double)budget / size * 0.95);
                current = (int)Math.Max(1000L, Math.Min(current - 500L, reduced));
            }
            scene.shapes = original;
            if (File.Exists(output)) File.Delete(output);                 // 最终版本由调用方重新写出
            if (best > 0) return best;
            return current;
        }

        private static MeshShape[] SimplifyAll(MeshShape[] shapes, int total)
        {
            int sum = shapes.Sum(s => s.triangles.Length);
            var result = new MeshShape[shapes.Length];
            for (int i = 0; i < shapes.Length; i++)
            {
                int share = sum == 0 ? total : (int)Math.Max(4L, Math.Round(total * (double)shapes[i].triangles.Length / sum));
                // 严格折痕保不住体积目标时逐级放宽，避免"填了更小的数却没有效果"。
                var mesh = MeshSimplify.Simplify(shapes[i], share, 30.0);
                double[] relax = { 45.0, 60.0, 75.0 };
                for (int step = 0; step < relax.Length && mesh.triangles.Length > share * 1.02; step++)
                {
                    // Rebuild each attempt from the full input: a coarse curved
                    // facet must not become a new source plane or normal field.
                    var candidate = MeshSimplify.Simplify(shapes[i], share, relax[step]);
                    if (candidate.triangles.Length < mesh.triangles.Length) mesh = candidate;
                }
                result[i] = mesh;
            }
            return result;
        }

        private static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            try
            {
                // Capability query performs no document access or conversion.
                if (args.Length == 1 && args[0] == "--protocol")
                {
                    Console.WriteLine("TxTools.CatiaConverter/2");
                    Console.WriteLine("quality mesh meshhash cgr stepmesh assembly");
                    return 0;
                }
                if (args.Length < 3) throw new ArgumentException("格式转换缺少源文件、目标 JT 或源文件摘要。");
                int simplify = 0, targetKb = 0; bool quality = false, cgr = false, skipHidden = false; string meshFile = null, meshHash = null, dumpMesh = null, objFile = null, stepMesh = null, assembly = null;
                for (int i = 3; i < args.Length; i++)
                {
                    if (args[i] == "flat") Jt8Writer.FlatStrips = true;
                    else if (args[i] == "quality") quality = true;
                    else if (args[i] == "cgr") cgr = true;
                    else if (args[i] == "skiphidden") skipHidden = true;
                    else if (args[i].StartsWith("assembly=", StringComparison.Ordinal)) assembly = args[i].Substring("assembly=".Length);
                    else if (args[i].StartsWith("stepmesh=", StringComparison.Ordinal)) stepMesh = args[i].Substring("stepmesh=".Length);
                    else if (args[i].StartsWith("mesh=", StringComparison.Ordinal)) meshFile = args[i].Substring(5);
                    else if (args[i].StartsWith("meshhash=", StringComparison.Ordinal)) meshHash = args[i].Substring(9);
                    else if (args[i].StartsWith("dumpmesh=", StringComparison.Ordinal)) dumpMesh = args[i].Substring(9);
                    else if (args[i].StartsWith("objfile=", StringComparison.Ordinal)) objFile = args[i].Substring(8);
                    else if (args[i].StartsWith("simplify=", StringComparison.Ordinal))
                        simplify = Math.Max(0, int.Parse(args[i].Substring("simplify=".Length)));
                    else if (args[i].StartsWith("targetkb=", StringComparison.Ordinal))
                        targetKb = Math.Max(0, int.Parse(args[i].Substring("targetkb=".Length)));
                    else throw new ArgumentException("无法识别的转换参数：" + args[i]);
                }
                SourceMeshScene scene;
                var phase = System.Diagnostics.Stopwatch.StartNew();
                if (assembly != null)
                {
                    if (cgr || stepMesh != null || objFile != null || meshFile != null || meshHash != null)
                        throw new ArgumentException("装配网格清单不能与其他几何输入模式同时使用。");
                    scene = ReadAssembly(assembly, Path.GetFullPath(args[0]), args[2], skipHidden);
                }
                else if (stepMesh != null)
                {
                    if (cgr || objFile != null || meshFile != null || meshHash != null)
                        throw new ArgumentException("STEP 网格参数不能与其他几何输入模式同时使用。");
                    scene = StepMeshReader.Read(stepMesh, Path.GetFileNameWithoutExtension(args[1]), Path.GetFullPath(args[0]), args[2]);
                }
                else if (cgr)
                {
                    if (objFile != null || meshFile != null || meshHash != null) throw new ArgumentException("CGR 输入不能与外部网格参数同时使用。");
                    string source = Path.GetFullPath(args[0]);
                    byte[] data = File.ReadAllBytes(source);
                    string digest;
                    using (var sha = SHA256.Create())
                        digest = BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
                    if (digest != args[2]) throw new IOException("CGR 在转换前发生变化。");
                    string name = Path.GetFileNameWithoutExtension(source);
                    var mesh = ReadCgr(data, name, source, digest);
                    scene = new SourceMeshScene { name = name, sourceFile = source, sourceSha256 = digest, shapes = new[] { mesh } };
                }
                else if (objFile != null) scene = ObjScene(objFile, args[0], args[2]);
                else if (meshFile == null) scene = SourceMeshScene.ReadPart(args[0]);
                else
                {
                    if (SourceDigest.Hash(args[0]) != args[2]) throw new IOException("所选 CATPart 自读取后已变化，请重新读取来源。");
                    byte[] data = File.ReadAllBytes(meshFile);
                    using (var sha = SHA256.Create())
                        if (BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant() != meshHash)
                            throw new IOException("重新细分的几何文件已变化，请重新转换。");
                    string name = Path.GetFileNameWithoutExtension(args[0]);
                    try
                    {
                        var mesh = NativeCadReader.Mesh(data, name, "source/1", args[0], args[2]);
                        mesh = NativeCadReader.RemoveCollapsedFlaps(mesh);
                        scene = new SourceMeshScene { name = name, sourceFile = args[0], sourceSha256 = args[2], shapes = new[] { mesh } };
                        Console.WriteLine("native-tessellation triangles=" + mesh.triangles.Length);
                    }
                    catch (Exception error) when (error is InvalidDataException || error is NotSupportedException)
                    {
                        Console.Error.WriteLine("重新细分网格暂不支持，已使用文档内嵌网格：" + error.Message);
                        scene = SourceMeshScene.ReadPart(args[0]);
                    }
                }
                long readMs = phase.ElapsedMilliseconds;
                if (scene.sourceSha256 != args[2]) throw new IOException("所选 CATPart 在转换期间发生变化，请重新读取来源。");
                if (dumpMesh != null) { DumpMesh(scene, dumpMesh); return 0; }
                if (targetKb > 0 && simplify == 0) simplify = TuneToSize(scene, args[1], targetKb);
                phase.Restart();
                if (simplify > 0) scene.shapes = SimplifyAll(scene.shapes, simplify);
                else if (quality) scene.shapes = scene.shapes.Select(shape => MeshSimplify.Optimize(shape)).ToArray();
                long simplifyMs = phase.ElapsedMilliseconds;
                phase.Restart();
                Jt8Writer.Write(scene, args[1]);
                long writeMs = phase.ElapsedMilliseconds;
                Console.WriteLine("timing ms[read=" + readMs + " simplify=" + simplifyMs + " write=" + writeMs + " total=" + (readMs + simplifyMs + writeMs) + "]");
                Console.WriteLine("simplify-profile " + MeshSimplify.Profile());
                Console.WriteLine("simplify=" + simplify + " targetkb=" + targetKb + " quality=" + quality + (quality ? " deviationMm=0.5" : "") + " triangles=" + scene.shapes.Sum(s => s.triangles.Length));
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }

        private static MeshShape ReadCgr(byte[] data, string name, string source, string digest)
        {
            if (data.Length >= 8 && Encoding.ASCII.GetString(data, 0, 8) == "V5_CFV3\0")
                return Cfv3MeshReader.Read(data, name, source, digest);
            byte[] graph = NativeCadReader.CgrSceneGraph(data);
            return graph.Length > 0 && graph[0] == 0x62
                ? Cfv3MeshReader.ReadCompactCgr(graph, name, source, digest)
                : NativeCadReader.MeshFromGraph(graph, name, "source/1", source, digest);
        }

        private static SourceMeshScene ReadAssembly(string manifest, string source, string digest, bool skipHidden)
        {
            AssemblyStructure structure;
            using (var stream = File.OpenRead(manifest)) structure = AssemblyManifest.Read(stream,
                Path.GetExtension(source).Equals(".catproduct", StringComparison.OrdinalIgnoreCase));
            // Shared definitions plus placement frames keep repeated geometry from
            // being copied per occurrence. Non-rigid frames need baked geometry.
            if (structure.Root != null && structure.Parts.All(record => IsRigid(record.Frame)))
                return ReadInstancedAssembly(structure, source, digest, skipHidden);
            return ReadBakedAssembly(structure, source, digest, skipHidden);
        }

        private static bool IsRigid(double[] frame)
        {
            if (frame == null || frame.Length != 12 || frame.Any(value => double.IsNaN(value) || double.IsInfinity(value)))
                return false;
            double Dot(int first, int second) => frame[first] * frame[second] + frame[first + 1] * frame[second + 1] +
                frame[first + 2] * frame[second + 2];
            double unit = 1e-5;
            return Math.Abs(Dot(0, 0) - 1) <= unit && Math.Abs(Dot(3, 3) - 1) <= unit &&
                Math.Abs(Dot(6, 6) - 1) <= unit && Math.Abs(Dot(0, 3)) <= unit &&
                Math.Abs(Dot(0, 6)) <= unit && Math.Abs(Dot(3, 6)) <= unit;
        }

        private static SourceMeshScene ReadInstancedAssembly(AssemblyStructure structure, string source, string digest, bool skipHidden)
        {
            string extension = Path.GetExtension(source).ToLowerInvariant();
            bool step = extension == ".step" || extension == ".stp";
            bool nativeProduct = extension == ".catproduct";
            bool threeDXml = extension == ".3dxml";
            var records = structure.Parts;
            var definitions = new List<MeshShape[]>();
            var definitionIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var skippedRepresentations = new Dictionary<string, (string Name, int Count, string Reason)>(StringComparer.OrdinalIgnoreCase);
            var instances = new AssemblyInstance[records.Length];
            var shapes = new List<MeshShape>();
            string warningPrefix = step ? "STEP-WARNING " : threeDXml ? "3DXML-WARNING " : "JT-WARNING ";
            long removed = 0;
            for (int recordIndex = 0; recordIndex < records.Length; recordIndex++)
            {
                var record = records[recordIndex];
                string path = record.GeometryPath, name = record.Name;
                if (!definitionIndex.TryGetValue(path, out int definition))
                {
                    MeshShape[] meshes;
                    if (nativeProduct)
                    {
                        meshes = SourceMeshScene.ReadPart(path).shapes;
                        if (meshes.Length != 0 && meshes[0].sourceSha256 != record.Digest)
                            throw new IOException("CATPart 文件与装配索引不一致：" + path);
                    }
                    else if (step)
                        meshes = StepMeshReader.Read(path, name, source, digest).shapes;
                    else
                    {
                        try { meshes = ReadRepresentation(path, name, source, digest, skipHidden); }
                        catch (Exception error) when (threeDXml && (error is InvalidDataException ||
                            error is NotSupportedException || error is InvalidOperationException ||
                            error is XmlException || error is FormatException || error is OverflowException))
                        {
                            meshes = Array.Empty<MeshShape>();
                            skippedRepresentations.Add(path, (name, 0, error.Message.Replace('\r', ' ').Replace('\n', ' ')));
                        }
                    }
                    var cleaned = new List<MeshShape>(meshes.Length);
                    int meshIndex = 0;
                    foreach (var mesh in meshes)
                    {
                        var filtered = mesh.RemoveDegenerateTriangles();
                        removed += mesh.triangles.Length - (filtered == null ? 0 : filtered.triangles.Length);
                        if (filtered == null) continue;
                        filtered.instanceId = "definition/" + (definitions.Count + 1) + "/" + (++meshIndex);
                        cleaned.Add(filtered);
                        shapes.Add(filtered);
                    }
                    definition = definitions.Count;
                    definitions.Add(cleaned.ToArray());
                    definitionIndex.Add(path, definition);
                }
                if (skippedRepresentations.TryGetValue(path, out var skipped))
                    skippedRepresentations[path] = (skipped.Name, skipped.Count + 1, skipped.Reason);
                instances[recordIndex] = new AssemblyInstance { Definition = definition, Name = name, Frame = record.Frame };
            }
            if (SourceDigest.Hash(source) != digest) throw new IOException("装配源文件在转换期间发生变化。");
            foreach (var skipped in skippedRepresentations.Values)
                Console.WriteLine("3DXML-WARNING 已跳过 " + skipped.Name + "（" + skipped.Count + " 个实例）：" + skipped.Reason);
            if (shapes.Count == 0) throw new InvalidDataException("装配没有可写入 JT 的面几何。");
            if (removed != 0) Console.WriteLine(warningPrefix + "已清理 " + removed + " 个在源网格或 JT 精度下退化的三角面。");
            return new SourceMeshScene { name = Path.GetFileNameWithoutExtension(source), sourceFile = source,
                sourceSha256 = digest, shapes = shapes.ToArray(), structure = structure.Root,
                definitions = definitions.ToArray(), instances = instances };
        }

        private static SourceMeshScene ReadBakedAssembly(AssemblyStructure structure, string source, string digest, bool skipHidden)
        {
            string extension = Path.GetExtension(source).ToLowerInvariant();
            bool step = extension == ".step" || extension == ".stp";
            bool nativeProduct = extension == ".catproduct";
            bool threeDXml = extension == ".3dxml";
            var records = structure.Parts;
            var definitions = new Dictionary<string, MeshShape[]>(StringComparer.OrdinalIgnoreCase);
            var skippedRepresentations = new Dictionary<string, (string Name, int Count, string Reason)>(StringComparer.OrdinalIgnoreCase);
            var shapes = new List<MeshShape>();
            var partShapes = new List<MeshShape[]>(records.Length);
            string warningPrefix = step ? "STEP-WARNING " : threeDXml ? "3DXML-WARNING " : "JT-WARNING ";
            long removed = 0;
            foreach (var record in records)
            {
                string path = record.GeometryPath, name = record.Name;
                string partDigest = nativeProduct ? record.Digest : null;
                if (!definitions.TryGetValue(path, out var meshes))
                {
                    if (nativeProduct)
                        meshes = SourceMeshScene.ReadPart(path).shapes;
                    else if (step)
                        meshes = StepMeshReader.Read(path, name, source, digest).shapes;
                    else
                    {
                        try { meshes = ReadRepresentation(path, name, source, digest, skipHidden); }
                        catch (Exception error) when (threeDXml && (error is InvalidDataException ||
                            error is NotSupportedException || error is InvalidOperationException ||
                            error is XmlException || error is FormatException || error is OverflowException))
                        {
                            meshes = Array.Empty<MeshShape>();
                            skippedRepresentations.Add(path, (name, 0, error.Message.Replace('\r', ' ').Replace('\n', ' ')));
                        }
                    }
                    definitions.Add(path, meshes);
                }
                var baked = new List<MeshShape>();
                if (skippedRepresentations.TryGetValue(path, out var skipped))
                {
                    skippedRepresentations[path] = (skipped.Name, skipped.Count + 1, skipped.Reason);
                    partShapes.Add(baked.ToArray());
                    continue;
                }
                if (nativeProduct && meshes[0].sourceSha256 != partDigest)
                    throw new IOException("CATPart 文件与装配索引不一致：" + path);
                var transform = new Frame12(record.Frame);
                foreach (var mesh in meshes)
                {
                    int collapsed = 0;
                    var instance = mesh.BakeInstanceForJt(transform, out collapsed);
                    removed += collapsed;
                    if (instance == null)
                    {
                        Console.WriteLine(warningPrefix + "已跳过 " + name + "（1 个实例）：装配坐标中的三角面全部退化。");
                        continue;
                    }
                    instance.name = name;
                    instance.instanceId = "source/" + (shapes.Count + 1);
                    shapes.Add(instance);
                    baked.Add(instance);
                }
                partShapes.Add(baked.ToArray());
            }
            if (SourceDigest.Hash(source) != digest) throw new IOException("装配源文件在转换期间发生变化。");
            foreach (var skipped in skippedRepresentations.Values)
                Console.WriteLine("3DXML-WARNING 已跳过 " + skipped.Name + "（" + skipped.Count + " 个实例）：" + skipped.Reason);
            if (shapes.Count == 0) throw new InvalidDataException("装配没有可写入 JT 的面几何。");
            if (removed != 0) Console.WriteLine(warningPrefix + "已清理 " + removed + " 个在装配坐标或 JT 精度下退化的三角面。");
            return new SourceMeshScene { name = Path.GetFileNameWithoutExtension(source), sourceFile = source,
                sourceSha256 = digest, shapes = shapes.ToArray(), structure = structure.Root, partShapes = partShapes.ToArray() };
        }

        private static MeshShape[] ReadRepresentation(string path, string name, string source, string digest, bool skipHidden)
        {
            var header = new byte[8];
            using (var file = File.OpenRead(path)) file.Read(header, 0, header.Length);
            string magic = Encoding.ASCII.GetString(header);
            if (magic == "V5_CFV2\0" || magic == "V5_CFV3\0")
                return new[] { ReadCgr(File.ReadAllBytes(path), name, source, digest) };
            return ThreeDRepMeshReader.Read(path, name, source, digest, skipHidden);
        }
    }

    internal static class ThreeDRepMeshReader
    {
        private static readonly char[] Separators = { ' ', '\t', '\r', '\n', ',', ';' };
        private static readonly byte[] DefaultColor = { 190, 190, 190 };

        private static bool Hidden(XElement element)
        {
            var ns = element.Name.Namespace;
            var attributes = element.Element(ns + "GeneralAttributes") ??
                element.Element(ns + "GraphicProperties")?.Element(ns + "GeneralAttributes");
            string value = ((string)attributes?.Attribute("visible"))?.Trim();
            return value == "false" || value == "0";
        }

        internal static MeshShape[] Read(string path, string name, string source, string digest, bool skipHidden)
        {
            XDocument document;
            using (var reader = XmlReader.Create(path, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null
            }))
                document = XDocument.Load(reader);
            var result = new List<MeshShape>();
            var ns = document.Root.Name.Namespace;
            foreach (var representation in document.Descendants(ns + "Rep"))
            {
                var faces = representation.Element(ns + "Faces");
                if (faces == null || (skipHidden && (Hidden(faces) || representation.AncestorsAndSelf().Any(Hidden)))) continue;
                var buffer = representation.Element(ns + "VertexBuffer");
                var points = Vectors((string)buffer?.Element(ns + "Positions"));
                var normals = Vectors((string)buffer?.Element(ns + "Normals"));
                if (points.Length == 0 || (normals.Length != 0 && normals.Length != points.Length))
                    throw new InvalidDataException("3DXML 顶点缓冲区无效：" + name);
                var triangles = new List<int[]>();
                var colors = new List<byte[]>();
                var repColor = Color(representation) ?? DefaultColor;
                foreach (var face in faces.Elements(ns + "Face"))
                {
                    if (skipHidden && Hidden(face)) continue;
                    var color = Color(face) ?? repColor;
                    int start = triangles.Count;
                    var indices = Indices((string)face.Attribute("triangles"));
                    if (indices.Length % 3 != 0) throw new InvalidDataException("3DXML 三角形索引数无效。");
                    for (int i = 0; i < indices.Length; i += 3)
                        triangles.Add(new[] { indices[i], indices[i + 1], indices[i + 2] });
                    AddStrips(triangles, (string)face.Attribute("strips"), false);
                    AddStrips(triangles, (string)face.Attribute("fans"), true);
                    for (int i = start; i < triangles.Count; i++) colors.Add(color);
                }
                if (triangles.Count == 0) continue;
                var mesh = new MeshShape
                {
                    name = name, instanceId = "rep/" + (result.Count + 1), sourceFile = source, sourceSha256 = digest,
                    vertices = points, triangles = triangles.ToArray(), rgb = colors.ToArray(),
                    normals = normals.Length == 0 ? null : normals, nativeNormalBindings = normals.Length != 0
                };
                mesh.Validate();
                if (mesh.normals == null) mesh.Smooth();
                result.Add(mesh);
            }
            if (result.Count == 0) throw new NotSupportedException((skipHidden ? "3DXML 表示没有可导入的可见三角面：" :
                "3DXML 表示没有可导入的三角面：") + name);
            return result.ToArray();
        }

        private static void AddStrips(List<int[]> triangles, string encoded, bool fan)
        {
            if (string.IsNullOrWhiteSpace(encoded)) return;
            foreach (var group in encoded.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var indices = Indices(group);
                for (int i = 2; i < indices.Length; i++)
                {
                    int a = fan ? indices[0] : indices[i - 2];
                    int b = indices[i - 1], c = indices[i];
                    if (a == b || a == c || b == c) continue;
                    triangles.Add(!fan && i % 2 != 0 ? new[] { b, a, c } : new[] { a, b, c });
                }
            }
        }

        private static int[] Indices(string text) => (text ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture)).ToArray();

        private static double[][] Vectors(string text)
        {
            var coordinates = (text ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries);
            if (coordinates.Length % 3 != 0) throw new InvalidDataException("3DXML 向量分量数无效。");
            var vectors = new double[coordinates.Length / 3][];
            for (int i = 0; i < vectors.Length; i++)
                vectors[i] = new[] { Number(coordinates[3 * i]), Number(coordinates[3 * i + 1]), Number(coordinates[3 * i + 2]) };
            return vectors;
        }

        private static double Number(string text)
        {
            double value = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new InvalidDataException("3DXML 数值无效。");
            return value;
        }

        private static byte[] Color(XElement owner)
        {
            var ns = owner.Name.Namespace;
            var color = owner.Element(ns + "SurfaceAttributes")?.Element(ns + "Color");
            if (color == null) return null;
            return new[] { "red", "green", "blue" }.Select(channel =>
                (byte)Math.Round(255 * Math.Max(0, Math.Min(1, Number((string)color.Attribute(channel)))))).ToArray();
        }
    }
}
