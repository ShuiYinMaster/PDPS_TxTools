using System;
using System.Collections.Generic;
using System.IO;

namespace TxTools.ExportByColor
{
    // Detached PS snapshots only. No SDK or CATIA object enters the encoder.
    public sealed class ColorGroup
    {
        public byte R, G, B;
        public int Surface;
        public List<float[]> Tris = new List<float[]>();
    }

    public static class PsCgrPipeline
    {
        public sealed class Result
        {
            public int Candidates, Cylinders, InnerCylinders, OuterCylinders;
        }

        public static Result BuildFile(List<ColorGroup> groups, string output, CgrBackend backend,
            bool reconstructCylinders, Action<string> log = null)
        {
            if (groups == null) throw new ArgumentNullException("groups");
            backend = CgrWriter.ResolveBackend(backend);
            if (reconstructCylinders && backend != CgrBackend.LineFace)
                throw new ArgumentException("PS cylinder reconstruction requires continuous line/face CGR", "backend");
            var vertices = new List<float[]>();
            var faces = new List<CgrWriter.Face>();
            var surfaces = new Dictionary<int, int>();
            var maps = new Dictionary<int, Dictionary<Tuple<float, float, float>, int>>();
            int nextSurface = 0;
            foreach (var group in groups)
            {
                if (group == null) throw new ArgumentException("Null triangle group", "groups");
                int surface;
                // Only the reconstruction route joins color patches belonging to
                // the same PS geometry. Unknown/independent identities stay apart.
                if (!reconstructCylinders || group.Surface <= 0 || !surfaces.TryGetValue(group.Surface, out surface))
                {
                    surface = checked(++nextSurface);
                    if (reconstructCylinders && group.Surface > 0) surfaces.Add(group.Surface, surface);
                }
                Dictionary<Tuple<float, float, float>, int> map;
                if (!maps.TryGetValue(surface, out map)) maps.Add(surface, map = new Dictionary<Tuple<float, float, float>, int>());
                foreach (var triangle in group.Tris)
                {
                    if (triangle == null || triangle.Length != 9) throw new ArgumentException("三角形数据必须为 9 个坐标值");
                    var ids = new int[3];
                    for (int k = 0; k < 3; k++)
                    {
                        var key = Tuple.Create(triangle[3 * k], triangle[3 * k + 1], triangle[3 * k + 2]);
                        int id;
                        if (!map.TryGetValue(key, out id))
                        {
                            id = vertices.Count; map.Add(key, id);
                            vertices.Add(new[] { key.Item1, key.Item2, key.Item3 });
                        }
                        ids[k] = id;
                    }
                    faces.Add(new CgrWriter.Face { Idx = ids, R = group.R, G = group.G, B = group.B, Surface = surface });
                }
            }
            if (faces.Count == 0) throw new InvalidOperationException("设备无有效三角面");
            var result = new Result();
            if (!reconstructCylinders)
            {
                CgrWriter.BuildFile(vertices, faces, output, backend, progress: log);
                return result;
            }
            CgrWriter.PreparePsReconstructionMesh(ref vertices, ref faces, log);
            var candidates = RoundHoleReconstruction.Find(vertices, faces, new RoundHoleReconstruction.Options
            {
                IncludeExternalCylinders = true, IncludeTransitionRims = true,
                IncludeOpenCylinderEnds = true, IncludeTrimmedCylinderEnds = true
            });
            result.Candidates = candidates.Count;
            var replacement = RoundHoleCgrMesh.Replace(vertices, faces, candidates, log);
            result.Cylinders = replacement.Cylinders.Count;
            foreach (var cylinder in replacement.Cylinders.Values)
                if (cylinder.ExternalCylinder) result.OuterCylinders++; else result.InnerCylinders++;
            // Prepared PS winding is retained. Synthetic reverse-side triangles
            // would corrupt the wall fit and duplicate analytic face records.
            if (result.Cylinders == 0)
                CgrWriter.BuildFile(vertices, faces, output, CgrBackend.LineFace, 20000, log, true);
            else
                CgrWriter.BuildFileWithCylinders(replacement.Vertices, replacement.Faces, output, replacement.Cylinders, log);
            File.WriteAllLines(output + ".cylinders.log", replacement.Diagnostics);
            if (log != null) log("[PS cylinders] candidates=" + result.Candidates + "; accepted=" + result.Cylinders
                + "; inner=" + result.InnerCylinders + "; outer=" + result.OuterCylinders
                + "; continuous line/face compression; unsupported regions retain source mesh");
            return result;
        }
    }
}
