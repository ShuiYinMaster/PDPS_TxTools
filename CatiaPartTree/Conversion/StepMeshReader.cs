using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TxTools.CatiaPartTree.Conversion
{
    internal static class StepMeshReader
    {
        // One vertex array per CAD face preserves normals at hard boundaries.
        internal static SourceMeshScene Read(string path, string name, string source, string digest)
        {
            var points = new List<double[]>();
            var normals = new List<double[]>();
            var triangles = new List<int[]>();
            var colors = new List<byte[]>();
            using (var file = File.OpenRead(path))
            using (var reader = new BinaryReader(file))
            {
                if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != "TXSTPM01")
                    throw new InvalidDataException("STEP 网格文件头无效。");
                int vertices;
                while ((vertices = reader.ReadInt32()) != 0)
                {
                    int faces = reader.ReadInt32();
                    if (vertices < 3 || faces < 1 || 3L + 48L * vertices + 12L * faces + 4 > file.Length - file.Position ||
                        vertices > int.MaxValue - points.Count || faces > int.MaxValue - triangles.Count)
                        throw new InvalidDataException("STEP 面网格范围无效。");
                    byte[] rgb = reader.ReadBytes(3);
                    int start = points.Count;
                    for (int i = 0; i < vertices; i++)
                    {
                        points.Add(ReadVector(reader));
                        double[] normal = ReadVector(reader);
                        normals.Add(normal.All(n => n == 0) ? null : normal);
                    }
                    for (int i = 0; i < faces; i++)
                    {
                        var face = new[] { reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32() };
                        if (face.Any(index => index < 0 || index >= vertices))
                            throw new InvalidDataException("STEP 三角索引超出顶点范围。");
                        triangles.Add(new[] { start + face[0], start + face[1], start + face[2] });
                        colors.Add(rgb);
                    }
                }
                if (file.Position != file.Length) throw new InvalidDataException("STEP 网格存在额外数据。");
            }
            if (triangles.Count == 0) throw new NotSupportedException("STEP 零件没有可转换的三角面。");
            var mesh = new MeshShape { name = name, instanceId = "source/1", sourceFile = source, sourceSha256 = digest,
                vertices = points.ToArray(), normals = normals.ToArray(), triangles = triangles.ToArray(), rgb = colors.ToArray(),
                nativeNormalBindings = normals.All(n => n != null) };
            mesh.Validate();
            mesh.FillMissingNormals();
            return new SourceMeshScene { name = name, sourceFile = source, sourceSha256 = digest, shapes = new[] { mesh } };
        }

        private static double[] ReadVector(BinaryReader reader)
        {
            var vector = new[] { reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble() };
            if (vector.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                throw new InvalidDataException("STEP 网格坐标或法向无效。");
            return vector;
        }
    }
}
