using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

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
                    Console.WriteLine("quality mesh meshhash");
                    return 0;
                }
                if (args.Length < 3) throw new ArgumentException("格式转换缺少源 CATPart、目标 JT 或源文件摘要。");
                int simplify = 0, targetKb = 0; bool quality = false; string meshFile = null, meshHash = null, dumpMesh = null, objFile = null;
                for (int i = 3; i < args.Length; i++)
                {
                    if (args[i] == "flat") Jt8Writer.FlatStrips = true;
                    else if (args[i] == "quality") quality = true;
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
                if (objFile != null) scene = ObjScene(objFile, args[0], args[2]);
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
    }
}
