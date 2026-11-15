using System;
using System.Collections.Generic;
using System.Linq;

namespace TxTools.CatiaPartTree.Conversion
{
    // QEM runs on geometric topology, not CATIA's normal/patch-split vertex IDs.
    // Only identical positions are joined. Legal contractions remove the two
    // shared faces, preserve edge incidence, and cannot create duplicate faces.
    // Real open boundaries stay fixed; normal seams are restored in the output.
    public static class MeshSimplify
    {
        const double SmoothCos = 0.8660254037844386; // 30 degrees
        const double SourceShadeCos = 0.5; // 60 degrees, only for the validated dense field
        sealed class Edge { public double Cost; public int A, B, VersionA, VersionB; public double[] Placement; }

        sealed class EdgeHeap
        {
            private readonly List<Edge> _items = new List<Edge>();
            public int Count { get { return _items.Count; } }
            public void Push(Edge edge)
            {
                _items.Add(edge);
                int i = _items.Count - 1;
                while (i > 0)
                {
                    int parent = (i - 1) >> 1;
                    if (_items[parent].Cost <= _items[i].Cost) break;
                    Swap(parent, i); i = parent;
                }
            }
            public Edge Pop()
            {
                var top = _items[0];
                int last = _items.Count - 1;
                _items[0] = _items[last]; _items.RemoveAt(last);
                int i = 0;
                while (true)
                {
                    int left = (i << 1) + 1, right = left + 1, small = i;
                    if (left < _items.Count && _items[left].Cost < _items[small].Cost) small = left;
                    if (right < _items.Count && _items[right].Cost < _items[small].Cost) small = right;
                    if (small == i) break;
                    Swap(i, small); i = small;
                }
                return top;
            }
            private void Swap(int a, int b) { var t = _items[a]; _items[a] = _items[b]; _items[b] = t; }
        }

        public static MeshShape Simplify(MeshShape mesh, int targetTriangles)
        {
            return Simplify(mesh, targetTriangles, 30.0);
        }

        // creaseDegrees: 超过该夹角视为硬边，不允许跨塌。体积目标很低时可由调用方逐级放宽。
        public static MeshShape Simplify(MeshShape mesh, int targetTriangles, double creaseDegrees)
        {
            return SimplifyCore(mesh, targetTriangles, creaseDegrees, false);
        }

        // Production mode has no byte or face-count target. Exhaust legal
        // contractions within geometric and shading error limits instead.
        public static MeshShape Optimize(MeshShape mesh)
        {
            return RestoreSourceNormals(mesh, SimplifyCore(mesh, 4, 75.0, true));
        }

        // Preserve the decoded CAD normal field after geometry optimization.
        // A reconstructed fan is only a fallback: one unreliable neighboring
        // sliver must not replace every valid analytic corner around a vertex.
        public static MeshShape RestoreSourceNormals(MeshShape source, MeshShape target)
        {
            if (!source.nativeNormalBindings || source.normals == null || source.normals.Length != source.vertices.Length) return target;
            double[][] p; int[][] face; BuildTopology(source, out p, out face);
            var unit = face.Select(t => NormalAt(p, t, -1, null)).ToArray();
            var incident = p.Select(v => new List<int>()).ToArray();
            for (int f = 0; f < face.Length; f++) foreach (int v in face[f]) incident[v].Add(f);
            var corners = SmoothFans(source, face, p, incident, Enumerable.Repeat(true, face.Length).ToArray(), new bool[p.Length], unit);
            var valid = ConsistentCacheFaces(source, face, p, unit);
            for (int f = 0; f < face.Length; f++) if (valid[f])
                for (int k = 0; k < 3; k++) corners[f * 3 + k] = source.normals[source.triangles[f][k]];
            var surface = new SourceSurface(source, unit, corners, face, p.Length);
            double[][] output; int[][] triangles; BuildTopology(target, out output, out triangles);
            var vertices = new List<double[]>(); var normals = new List<double[]>();
            var lookup = new Dictionary<Tuple<int, double, double, double>, int>();
            var result = new int[triangles.Length][];
            for (int f = 0; f < triangles.Length; f++)
            {
                result[f] = new int[3];
                var stored = triangles[f].Select(i => output[i].Select(x => (double)(float)x).ToArray()).ToArray();
                var direction = NormalAt(stored, new[] { 0, 1, 2 }, -1, null);
                for (int k = 0; k < 3; k++)
                {
                    int vertex = triangles[f][k]; double[] n, point;
                    var previous = target.normals == null ? direction : target.normals[target.triangles[f][k]] ?? direction;
                    if (!surface.Sample(output[vertex], previous, out n, out point)
                        || Dot(n, direction) < SourceShadeCos
                        || output[vertex].Zip(point, (a, b) => (a - b) * (a - b)).Sum() > 0.0025) n = previous;
                    var key = Tuple.Create(vertex, n[0], n[1], n[2]); int index;
                    if (!lookup.TryGetValue(key, out index))
                    { index = vertices.Count; lookup.Add(key, index); vertices.Add(output[vertex]); normals.Add(n); }
                    result[f][k] = index;
                }
            }
            return new MeshShape { name = target.name, instanceId = target.instanceId, sourceFile = target.sourceFile, sourceSha256 = target.sourceSha256,
                vertices = vertices.ToArray(), triangles = result, normals = normals.ToArray(), rgb = target.rgb };
        }

        static MeshShape SimplifyCore(MeshShape mesh, int targetTriangles, double creaseDegrees, bool quality)
        {
            if (mesh == null || targetTriangles <= 0 || mesh.triangles.Length <= targetTriangles) return mesh;

            double[][] position; int[][] face;
            BuildTopology(mesh, out position, out face);
            var alive = Enumerable.Repeat(true, face.Length).ToArray();
            int live = face.Length;
            int count = position.Length;
            var errorOrigin = new double[3];
            double spanSquared = 0, magnitudeSquared = 0;
            for (int k = 0; k < 3; k++)
            {
                double lo = position.Min(p => p[k]), hi = position.Max(p => p[k]);
                errorOrigin[k] = (lo + hi) * 0.5;
                spanSquared += (hi - lo) * (hi - lo);
                double absolute = Math.Max(Math.Abs(lo), Math.Abs(hi)); magnitudeSquared += absolute * absolute;
            }
            double density = Math.Max(64, targetTriangles);
            // Bound displacement against the full source, not the preceding
            // collapse. The floor accounts for float32 output coordinates.
            double distance = quality ? Math.Max(0.01, Math.Min(0.05, Math.Sqrt(spanSquared) * 0.00005)) : Math.Sqrt(spanSquared) / density;
            double sourceDistanceSquared = Math.Max(distance * distance, magnitudeSquared * 5.684341886080802e-14);
            double lightingCos = quality ? 0.9998476951563913 : 0.9781476007338057; // 1 / 12 degrees
            var supports = quality ? position.Select(p => new List<double[]> { p }).ToArray() : null;
            if (supports != null) foreach (var t in face)
                supports[t[0]].Add(new[] { (position[t[0]][0] + position[t[1]][0] + position[t[2]][0]) / 3,
                    (position[t[0]][1] + position[t[1]][1] + position[t[2]][1]) / 3,
                    (position[t[0]][2] + position[t[1]][2] + position[t[2]][2]) / 3 });
            bool hadNormals = mesh.normals != null && mesh.normals.Length == mesh.vertices.Length;
            var touched = hadNormals ? new bool[count] : null;

            // 对称 4x4 平面二次型，每顶点扁平存储 10 个分量：
            // [xx xy xz xw yy yz yw zz zw ww]，w=1。
            var quadric = new double[10 * count];
            // 源网格面法向：翻折校验的绝对参照。单步 45° 限制挡不住多步累计
            // 翻转（三步就能把面翻到背面，渲染成空洞），相对源法向判同侧才行。
            var sourceNormal = new double[face.Length][];
            for (int f = 0; f < face.Length; f++)
            {
                double[] a = position[face[f][0]], b = position[face[f][1]], c = position[face[f][2]];
                double nx = (b[1] - a[1]) * (c[2] - a[2]) - (b[2] - a[2]) * (c[1] - a[1]);
                double ny = (b[2] - a[2]) * (c[0] - a[0]) - (b[0] - a[0]) * (c[2] - a[2]);
                double nz = (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]);
                double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (length == 0) continue;
                sourceNormal[f] = new[] { nx / length, ny / length, nz / length };
                double px = nx / length, py = ny / length, pz = nz / length;
                double pd = -(px * (a[0] - errorOrigin[0]) + py * (a[1] - errorOrigin[1]) + pz * (a[2] - errorOrigin[2]));
                for (int k = 0; k < 3; k++)
                {
                    int o = 10 * face[f][k];
                    quadric[o] += px * px; quadric[o + 1] += px * py; quadric[o + 2] += px * pz; quadric[o + 3] += px * pd;
                    quadric[o + 4] += py * py; quadric[o + 5] += py * pz; quadric[o + 6] += py * pd;
                    quadric[o + 7] += pz * pz; quadric[o + 8] += pz * pd; quadric[o + 9] += pd * pd;
                }
            }

            var incident = new List<int>[count];
            for (int i = 0; i < incident.Length; i++) incident[i] = new List<int>(6);
            for (int f = 0; f < face.Length; f++) for (int k = 0; k < 3; k++) incident[face[f][k]].Add(f);

            // Freeze open boundary vertices: hole rims and sheet edges must not move.
            var use = new Dictionary<long, int>(face.Length * 2);
            for (int f = 0; f < face.Length; f++)
            {
                for (int k = 0; k < 3; k++)
                {
                    int u = face[f][k], v = face[f][(k + 1) % 3];
                    long key = u < v ? ((long)u << 32) | (uint)v : ((long)v << 32) | (uint)u;
                    int n; use.TryGetValue(key, out n); use[key] = n + 1;
                }
            }
            var rim = new bool[count];
            foreach (var pair in use)
            {
                if (pair.Value == 2) continue;
                rim[(int)(pair.Key >> 32)] = true;
                rim[(int)(pair.Key & 0xFFFFFFFF)] = true;
            }
            AddFeatureQuadrics(quadric, position, face, incident, use, sourceNormal, errorOrigin);
            // Keep the dense source's smooth normal field. Reconstructing it from
            // coarse chord triangles alone gives rounded bosses a polygonal light
            // pattern even when their shared-edge normals are continuous.
            var sourceCorners = SmoothFans(mesh, face, position, incident, alive, touched, sourceNormal);
            var planar = new PlanarRegions(position, face, incident, sourceNormal, sourceCorners, mesh);
            planar.AnchorSourceNormals(face, incident, sourceNormal, sourceCorners);
            var sourceSurface = new SourceSurface(mesh, sourceNormal, sourceCorners, face, count);
            var lightingCorners = (double[][])sourceCorners.Clone();
            AddCurvatureQuadrics(quadric, position, face, sourceNormal, sourceCorners, errorOrigin);

            var version = new int[count];
            var scratch = new double[10];
            var heap = new EdgeHeap();
            Action<int, int> push = (u, v) =>
            {
                int a = u < v ? u : v, b = u < v ? v : u;
                double[] placement;
                double cost = EdgeCost(quadric, position, scratch, a, b, rim, errorOrigin, out placement);
                heap.Push(new Edge { Cost = cost, A = a, B = b, VersionA = version[a], VersionB = version[b], Placement = placement });
            };
            for (int f = 0; f < face.Length; f++)
            {
                push(face[f][0], face[f][1]); push(face[f][1], face[f][2]); push(face[f][2], face[f][0]);
            }

            double creaseCos = Math.Cos(Math.Max(5.0, Math.Min(80.0, creaseDegrees)) * Math.PI / 180.0);
            const double FoldCos = 0.7071067811865476;     // 45 degrees: reject folding
            // Prefer round transitions over collapsing them into long chords.
            // If the 15-degree source-normal budget exhausts all candidates,
            // relax it to 30 degrees without moving any real boundary.
            double sourceCos = 0.9659258262890683;
            bool relaxedCurvature = false;
            while (live > targetTriangles)
            {
                if (heap.Count == 0)
                {
                    if (relaxedCurvature) break;
                    relaxedCurvature = true; sourceCos = SmoothCos;
                    for (int f = 0; f < face.Length; f++)
                    {
                        if (!alive[f]) continue;
                        push(face[f][0], face[f][1]); push(face[f][1], face[f][2]); push(face[f][2], face[f][0]);
                    }
                    if (heap.Count == 0) break;
                }
                var edge = heap.Pop();
                int u = edge.A, v = edge.B;
                if (edge.VersionA != version[u] || edge.VersionB != version[v]) continue;   // stale entry

                var shared = new List<int>(2);
                foreach (int f in incident[u])
                {
                    if (!alive[f]) continue;
                    int[] t = face[f];
                    if (t[0] == v || t[1] == v || t[2] == v) shared.Add(f);
                }
                if (shared.Count != 2 || rim[u] || rim[v]) continue;
                double dihedral = 1.0;
                double[] referenceNormal;
                {
                    var n0 = NormalAt(position, face[shared[0]], -1, null);
                    var n1 = NormalAt(position, face[shared[1]], -1, null);
                    if (n0 == null || n1 == null) continue;
                    dihedral = n0[0] * n1[0] + n0[1] * n1[1] + n0[2] * n1[2];
                    if (dihedral < creaseCos) continue;                                       // hard edge
                    referenceNormal = Unit(n0[0] + n1[0], n0[1] + n1[1], n0[2] + n1[2]);
                }
                Dictionary<long, int> edgeChanges;
                if (!TopologyAllows(face, alive, incident, use, u, v, shared, out edgeChanges)) continue;

                var choice = planar.Choose(position, face, incident, alive, u, v, edge.Placement);
                if (choice == null) continue;
                var originalPlacement = choice.Point; var placement = originalPlacement;
                double[] sampledNormal, projected;
                if (choice.Project && referenceNormal != null && sourceSurface.SampleVertex(placement, referenceNormal, u, out sampledNormal, out projected)
                    && Squared(projected, placement) <= Squared(position[u], position[v]) * 0.0625)
                    placement = projected;
                // Surface projection is bounded by one quarter of this edge and
                // has the same fold/normal checks as its segment-constrained seed.
                // Fall back to that seed when projection would damage a neighbor.
                bool ok = PlacementAllows(position, face, alive, incident, sourceNormal, u, v, shared, placement, FoldCos, sourceCos);
                if (!ok && !object.ReferenceEquals(placement, originalPlacement))
                { placement = originalPlacement; ok = PlacementAllows(position, face, alive, incident, sourceNormal, u, v, shared, placement, FoldCos, sourceCos); }
                if (!ok) continue;
                Dictionary<int, double[]> lightingChanges;
                if (!LightingAllows(position, face, alive, incident, sourceNormal, lightingCorners, planar, sourceSurface,
                    u, v, shared, placement, sourceDistanceSquared, lightingCos, quality, out lightingChanges)) continue;
                if (supports != null && !CoverageAllows(position, face, alive, incident, supports, u, v, shared, placement, sourceDistanceSquared)) continue;

                foreach (var change in lightingChanges) lightingCorners[change.Key] = change.Value;
                if (supports != null) { supports[u].AddRange(supports[v]); supports[v].Clear(); }
                planar.Commit(choice);
                foreach (int f in shared) { alive[f] = false; live--; }
                foreach (var change in edgeChanges)
                {
                    int before; use.TryGetValue(change.Key, out before);
                    int next = before + change.Value;
                    if (next == 0) use.Remove(change.Key); else use[change.Key] = next;
                }
                position[u] = (double[])placement.Clone();
                foreach (int f in incident[v])
                {
                    if (!alive[f]) continue;
                    int[] t = face[f];
                    for (int k = 0; k < 3; k++) if (t[k] == v) t[k] = u;
                    incident[u].Add(f);
                }
                incident[v].Clear();
                int ou = 10 * u, ov = 10 * v;
                for (int k = 0; k < 10; k++) quadric[ou + k] += quadric[ov + k];
                version[u]++; version[v]++;
                if (touched != null) { touched[u] = true; touched[v] = true; }
                foreach (int f in incident[u].ToArray())
                {
                    if (!alive[f]) continue;
                    int[] t = face[f];
                    push(t[0], t[1]); push(t[1], t[2]); push(t[2], t[0]);
                }
            }
            var unitNormals = new double[face.Length][];
            // Shade the geometry that JT actually stores. Float32 rounding can
            // rotate a very thin face enough to invalidate a cache corner normal.
            var storedPosition = position.Select(p => p.Select(value => (double)(float)value).ToArray()).ToArray();
            for (int f = 0; f < face.Length; f++)
            {
                if (!alive[f]) continue;
                var t = face[f]; var a = storedPosition[t[0]]; var b = storedPosition[t[1]]; var c = storedPosition[t[2]];
                var raw = new[] { (b[1] - a[1]) * (c[2] - a[2]) - (b[2] - a[2]) * (c[1] - a[1]),
                    (b[2] - a[2]) * (c[0] - a[0]) - (b[0] - a[0]) * (c[2] - a[2]),
                    (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]) };
                double length = Math.Sqrt(Dot(raw, raw));
                if (length == 0) throw new InvalidOperationException("简化产生退化面，已停止输出。");
                unitNormals[f] = new[] { raw[0] / length, raw[1] / length, raw[2] / length };
            }
            var cornerNormals = SmoothFans(mesh, face, storedPosition, incident, alive, touched, unitNormals, sourceSurface, planar, position);
            var map = new Dictionary<VertexKey, int>();
            var vertices = new List<double[]>(count);
            var normals = new List<double[]>(count);
            var triangles = new List<int[]>(live);
            var colors = new List<byte[]>(live);
            for (int f = 0; f < face.Length; f++)
            {
                if (!alive[f]) continue;
                var t = new int[3];
                for (int k = 0; k < 3; k++)
                {
                    int index = face[f][k];
                    var normal = cornerNormals[f * 3 + k];
                    var key = new VertexKey(index, normal);
                    int output;
                    if (!map.TryGetValue(key, out output))
                    {
                        output = vertices.Count; map.Add(key, output);
                        vertices.Add(position[index]);
                        normals.Add(normal);
                    }
                    t[k] = output;
                }
                triangles.Add(t); colors.Add(mesh.rgb[f]);
            }
            var result = new MeshShape
            {
                name = mesh.name, instanceId = mesh.instanceId, sourceFile = mesh.sourceFile, sourceSha256 = mesh.sourceSha256,
                vertices = vertices.ToArray(), triangles = triangles.ToArray(), rgb = colors.ToArray(),
                normals = normals.ToArray()
            };
            result.Validate();
            return result;
        }

        struct PositionKey : IEquatable<PositionKey>
        {
            readonly double x, y, z;
            public PositionKey(double[] p) { x = p[0]; y = p[1]; z = p[2]; }
            public bool Equals(PositionKey other) { return x == other.x && y == other.y && z == other.z; }
            public override bool Equals(object other) { return other is PositionKey && Equals((PositionKey)other); }
            public override int GetHashCode() { unchecked { return (x.GetHashCode() * 397 ^ y.GetHashCode()) * 397 ^ z.GetHashCode(); } }
        }
        struct FaceKey : IEquatable<FaceKey>
        {
            readonly int a, b, c;
            public FaceKey(int x, int y, int z)
            { if (x > y) { int t = x; x = y; y = t; } if (y > z) { int t = y; y = z; z = t; } if (x > y) { int t = x; x = y; y = t; } a = x; b = y; c = z; }
            public bool Equals(FaceKey other) { return a == other.a && b == other.b && c == other.c; }
            public override bool Equals(object other) { return other is FaceKey && Equals((FaceKey)other); }
            public override int GetHashCode() { unchecked { return (a * 397 ^ b) * 397 ^ c; } }
        }
        struct VertexKey : IEquatable<VertexKey>
        {
            readonly int position;
            readonly PositionKey normal;
            public VertexKey(int index, double[] value) { position = index; normal = new PositionKey(value); }
            public bool Equals(VertexKey other) { return position == other.position && normal.Equals(other.normal); }
            public override bool Equals(object other) { return other is VertexKey && Equals((VertexKey)other); }
            public override int GetHashCode() { unchecked { return position * 397 ^ normal.GetHashCode(); } }
        }
        static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
        static double Squared(double[] a, double[] b)
        { double x = a[0] - b[0], y = a[1] - b[1], z = a[2] - b[2]; return x * x + y * y + z * z; }
        static bool PlacementAllows(double[][] p, int[][] face, bool[] alive, List<int>[] incident,
            double[][] source, int u, int v, List<int> shared, double[] placement, double foldCos, double sourceCos)
        {
            foreach (int vertex in new[] { v, u }) foreach (int f in incident[vertex])
            {
                if (!alive[f] || shared.Contains(f)) continue;
                var before = NormalAt(p, face[f], -1, null); var after = NormalAt(p, face[f], vertex, placement);
                if (before == null || after == null || source[f] == null
                    || Dot(before, after) < foldCos || Dot(after, source[f]) < sourceCos) return false;
            }
            return true;
        }
        static readonly double[][] LightingSamples = {
            new[] { 1d / 3, 1d / 3, 1d / 3 },
            new[] { .5, .5, 0 }, new[] { 0, .5, .5 }, new[] { .5, 0, .5 },
            new[] { 2d / 3, 1d / 6, 1d / 6 }, new[] { 1d / 6, 2d / 3, 1d / 6 }, new[] { 1d / 6, 1d / 6, 2d / 3 }
        };
        // Check both the interior and edges: a long transition triangle can
        // reproduce the center normal while missing curvature near an edge.
        static bool LightingAllows(double[][] p, int[][] face, bool[] alive, List<int>[] incident,
            double[][] source, double[][] corners, PlanarRegions planar, SourceSurface surface,
            int u, int v, List<int> shared, double[] placement, double distanceSquared, double lightingCos, bool refine, out Dictionary<int, double[]> changes)
        {
            changes = new Dictionary<int, double[]>();
            foreach (int vertex in new[] { v, u }) foreach (int f in incident[vertex])
            {
                if (!alive[f] || shared.Contains(f) || planar.FaceNormal(f) != null) continue;
                var t = face[f]; var points = new double[3][]; var normals = new double[3][]; bool sampled = true;
                for (int k = 0; k < 3; k++)
                {
                    int i = t[k]; var point = i == u || i == v ? placement : p[i];
                    var normal = corners[f * 3 + k];
                    if (i == u || i == v)
                    {
                        double[] projected;
                        if (!surface.SampleVertex(point, source[f], i, out normal, out projected)) { sampled = false; break; }
                        if (Squared(point, projected) > distanceSquared) return false;
                        changes[f * 3 + k] = normal;
                    }
                    points[k] = point; normals[k] = normal;
                }
                if (!sampled) continue;
                for (int sample = 0; sample < (refine ? LightingSamples.Length : 1); sample++)
                {
                    var weights = LightingSamples[sample]; var point = new double[3]; var sum = new double[3];
                    for (int k = 0; k < 3; k++) for (int axis = 0; axis < 3; axis++)
                    { point[axis] += points[k][axis] * weights[k]; sum[axis] += normals[k][axis] * weights[k]; }
                    double[] actual, closest;
                    var interpolated = Unit(sum[0], sum[1], sum[2]);
                    if (surface.SampleVertex(point, source[f], u, out actual, out closest)
                        && (Squared(point, closest) > distanceSquared || (interpolated != null && Dot(actual, interpolated) < lightingCos))) return false;
                }
            }
            return true;
        }
        // Retain all original samples belonging to the contracted vertices.
        // One-sided proximity alone permits sliding along a surface and losing
        // a narrow strip while every remaining vertex still lies on the source.
        static bool CoverageAllows(double[][] p, int[][] face, bool[] alive, List<int>[] incident,
            List<double[]>[] samples, int u, int v, List<int> shared, double[] placement, double limit)
        {
            var triangles = new List<double[][]>();
            foreach (int vertex in new[] { u, v }) foreach (int f in incident[vertex])
            {
                if (!alive[f] || shared.Contains(f)) continue;
                triangles.Add(face[f].Select(i => i == u || i == v ? placement : p[i]).ToArray());
            }
            foreach (int vertex in new[] { u, v }) foreach (var sample in samples[vertex])
            {
                bool covered = false;
                foreach (var t in triangles)
                {
                    double b, c;
                    if (SourceSurface.TriangleDistance(sample, t[0], t[1], t[2], out b, out c) <= limit) { covered = true; break; }
                }
                if (!covered) return false;
            }
            return true;
        }
        static double CornerAngle(double[][] p, int[] t, int corner)
        {
            var a = p[t[corner]]; var b = p[t[(corner + 1) % 3]]; var c = p[t[(corner + 2) % 3]];
            double x = b[0] - a[0], y = b[1] - a[1], z = b[2] - a[2];
            double u = c[0] - a[0], v = c[1] - a[1], w = c[2] - a[2];
            double nx = y * w - z * v, ny = z * u - x * w, nz = x * v - y * u;
            return Math.Atan2(Math.Sqrt(nx * nx + ny * ny + nz * nz), x * u + y * v + z * w);
        }
        static double[] Unit(double x, double y, double z)
        {
            double length = Math.Sqrt(x * x + y * y + z * z);
            return length > 0 ? new[] { x / length, y / length, z / length } : null;
        }
        static bool[] ConsistentCacheFaces(MeshShape mesh, int[][] face, double[][] p, double[][] unit)
        {
            if (mesh.normals == null || mesh.normals.Length != mesh.vertices.Length) return null;
            var valid = new bool[face.Length];
            const double TangentLimitSquared = 0.007596123493895969; // sin(5 degrees)^2
            for (int f = 0; f < face.Length; f++)
            {
                bool ok = unit[f] != null;
                for (int k = 0; k < 3 && ok; k++)
                {
                    int next = (k + 1) % 3;
                    var a = mesh.normals[mesh.triangles[f][k]]; var b = mesh.normals[mesh.triangles[f][next]];
                    if (a == null || b == null || Dot(a, unit[f]) < SourceShadeCos || Dot(b, unit[f]) < SourceShadeCos) { ok = false; break; }
                    var pa = p[face[f][k]]; var pb = p[face[f][next]];
                    double x = pb[0] - pa[0], y = pb[1] - pa[1], z = pb[2] - pa[2];
                    double nx = a[0] + b[0], ny = a[1] + b[1], nz = a[2] + b[2];
                    double tangent = x * nx + y * ny + z * nz;
                    ok = tangent * tangent <= (x * x + y * y + z * z) * (nx * nx + ny * ny + nz * nz) * TangentLimitSquared;
                }
                valid[f] = ok;
            }
            return valid;
        }
        // A smooth connected fan shares one normal at a geometric vertex. Using
        // corner angles rather than triangle areas prevents long strip triangles
        // from overwhelming the small triangles along a round transition.
        static double[][] SmoothFans(MeshShape mesh, int[][] face, double[][] p, List<int>[] incident,
            bool[] alive, bool[] touched, double[][] unit, SourceSurface surface = null, PlanarRegions planar = null, double[][] samplePosition = null)
        {
            var result = new double[face.Length * 3][];
            double cacheCos = surface == null ? SourceShadeCos : SmoothCos;
            var cacheFaces = surface == null ? ConsistentCacheFaces(mesh, face, p, unit) : null;
            for (int vertex = 0; vertex < incident.Length; vertex++)
            {
                var faces = incident[vertex].Where(f => alive[f]).ToArray();
                if (faces.Length == 0) continue;
                var parent = Enumerable.Range(0, faces.Length).ToArray();
                int Root(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
                var edges = new Dictionary<int, int>();
                var corners = new int[faces.Length]; var weights = new double[faces.Length];
                for (int i = 0; i < faces.Length; i++)
                {
                    int f = faces[i]; corners[i] = Array.IndexOf(face[f], vertex);
                    weights[i] = CornerAngle(p, face[f], corners[i]);
                    foreach (int neighbor in face[f])
                    {
                        if (neighbor == vertex) continue;
                        int other;
                        if (edges.TryGetValue(neighbor, out other))
                        { if (Dot(unit[f], unit[faces[other]]) >= SmoothCos) parent[Root(i)] = Root(other); }
                        else edges.Add(neighbor, i);
                    }
                }
                var groups = new Dictionary<int, List<int>>();
                for (int i = 0; i < faces.Length; i++)
                { int root = Root(i); List<int> members; if (!groups.TryGetValue(root, out members)) groups.Add(root, members = new List<int>()); members.Add(i); }
                foreach (var group in groups.Values)
                {
                    double x = 0, y = 0, z = 0; double[] cache = null; bool keepCache = surface == null && touched != null && !touched[vertex];
                    foreach (int i in group)
                    {
                        int f = faces[i]; double weight = weights[i];
                        if (cacheFaces != null && !cacheFaces[f]) keepCache = false;
                        x += unit[f][0] * weight; y += unit[f][1] * weight; z += unit[f][2] * weight;
                        var normal = touched == null ? null : mesh.normals[mesh.triangles[f][corners[i]]];
                        if (normal == null || Dot(normal, unit[f]) < cacheCos) keepCache = false;
                        else if (cache == null) cache = normal;
                        else if (!new PositionKey(cache).Equals(new PositionKey(normal))) keepCache = false;
                    }
                    var average = keepCache ? cache : Unit(x, y, z);
                    double shadeCos = keepCache ? cacheCos : SmoothCos;
                    double[] transferred, sourcePoint;
                    if (!keepCache && average != null && surface != null
                        && surface.SampleVertex((samplePosition ?? p)[vertex], average, vertex, out transferred, out sourcePoint)
                        && group.All(i => Dot(transferred, unit[faces[i]]) >= SourceShadeCos))
                    { average = transferred; shadeCos = SourceShadeCos; }
                    var planeNormal = planar == null ? null : planar.GroupNormal(group.Select(i => faces[i]));
                    // Snap only an already tangent sample; a neighboring plane
                    // must not overwrite a different direction on the curved side.
                    if (planeNormal != null && average != null && Dot(planeNormal, average) >= 0.9999984769132877)
                    { average = planeNormal; shadeCos = SourceShadeCos; }
                    foreach (int i in group)
                    {
                        int f = faces[i]; var normal = average;
                        // An under-resolved fan may span more than the safe cone.
                        // Reconstruct only its outliers from compatible fan faces.
                        if (normal == null || Dot(normal, unit[f]) < shadeCos)
                        {
                            x = y = z = 0;
                            foreach (int j in group)
                            {
                                int other = faces[j]; if (Dot(unit[f], unit[other]) < SmoothCos) continue;
                                x += unit[other][0] * weights[j]; y += unit[other][1] * weights[j]; z += unit[other][2] * weights[j];
                            }
                            normal = Unit(x, y, z) ?? unit[f];
                        }
                        var flat = planar == null ? null : planar.FaceNormal(f);
                        result[f * 3 + corners[i]] = flat != null && Dot(flat, unit[f]) >= SourceShadeCos ? flat : normal;
                    }
                }
            }
            return result;
        }

        // A plane needs coplanar connected faces and non-collinear support, not
        // just one triangle (or a rectangular facet of a cylindrical mesh).
        sealed class PlanarRegions
        {
            sealed class Plane { internal double[] Normal, Point; }
            internal sealed class Choice
            {
                internal double[] Point; internal bool Project;
                internal List<long> Remove;
                internal Dictionary<long, int[]> Add;
            }
            const double ParallelCos = 0.9993908270190958; // 2 degrees; position tests decide coplanarity
            const double AnchorCos = 0.9999999847691291;   // 0.01 degree
            readonly List<Plane> planes = new List<Plane>();
            readonly int[] region;
            readonly double[][] sourcePoints;
            readonly Dictionary<long, int[]> boundaryPaths = new Dictionary<long, int[]>();
            readonly double tolerance, boundaryTolerance;
            internal PlanarRegions(double[][] p, int[][] face, List<int>[] incident, double[][] normals, double[][] corners, MeshShape mesh)
            {
                sourcePoints = (double[][])p.Clone();
                var cachedFaces = ConsistentCacheFaces(mesh, face, p, normals);
                double diagonal = 0, magnitude = 0;
                for (int k = 0; k < 3; k++)
                {
                    double lo = p.Min(v => v[k]), hi = p.Max(v => v[k]), span = hi - lo, absolute = Math.Max(Math.Abs(lo), Math.Abs(hi));
                    diagonal += span * span; magnitude += absolute * absolute;
                }
                tolerance = Math.Max(Math.Sqrt(diagonal) * 1e-7, Math.Sqrt(magnitude) * 1.1920928955078125e-7);
                // Every original boundary sample stays in the path check, so
                // repeated collapses cannot accumulate unchecked rim shrink.
                boundaryTolerance = Math.Max(tolerance * 4, Math.Sqrt(diagonal) * 1e-5);
                region = Enumerable.Repeat(-1, face.Length).ToArray();
                var area = new double[face.Length];
                for (int f = 0; f < face.Length; f++)
                {
                    var t = face[f]; var a = p[t[0]]; var b = p[t[1]]; var c = p[t[2]];
                    double x = b[0] - a[0], y = b[1] - a[1], z = b[2] - a[2], u = c[0] - a[0], v = c[1] - a[1], w = c[2] - a[2];
                    area[f] = Math.Sqrt(Math.Pow(y * w - z * v, 2) + Math.Pow(z * u - x * w, 2) + Math.Pow(x * v - y * u, 2));
                }
                var visited = new bool[face.Length];
                foreach (int seed in Enumerable.Range(0, face.Length).OrderByDescending(f => area[f]).ThenBy(f => f))
                {
                    if (visited[seed] || normals[seed] == null) continue;
                    var n = normals[seed]; var anchor = p[face[seed][0]];
                    bool Fits(int f)
                    {
                        if (normals[f] == null || Dot(n, normals[f]) < ParallelCos) return false;
                        return face[f].All(i => Math.Abs(Signed(p[i], n, anchor)) <= tolerance);
                    }
                    var members = new List<int>(); var vertices = new HashSet<int>(); var queue = new Queue<int>();
                    visited[seed] = true; queue.Enqueue(seed);
                    while (queue.Count > 0)
                    {
                        int f = queue.Dequeue(); members.Add(f); var t = face[f];
                        foreach (int i in t) vertices.Add(i);
                        for (int k = 0; k < 3; k++)
                        {
                            int a = t[k], b = t[(k + 1) % 3];
                            foreach (int other in incident[a])
                            {
                                if (visited[other] || Array.IndexOf(face[other], b) < 0 || !Fits(other)) continue;
                                visited[other] = true; queue.Enqueue(other);
                            }
                        }
                    }
                    if (members.Count < 2) continue;
                    var support = new HashSet<int>(); var cachedSupport = new HashSet<int>();
                    foreach (int f in members) for (int k = 0; k < 3; k++)
                    {
                        var normal = corners[f * 3 + k];
                        if (normal != null && Dot(normal, n) >= AnchorCos * Math.Sqrt(Dot(normal, normal))) support.Add(face[f][k]);
                        if (cachedFaces != null && cachedFaces[f])
                        {
                            var cached = mesh.normals[mesh.triangles[f][k]];
                            if (cached != null && Dot(cached, n) >= AnchorCos * Math.Sqrt(Dot(cached, cached))) cachedSupport.Add(face[f][k]);
                        }
                    }
                    // Reconstructed geometric fan normals are not independent
                    // evidence of a CAD plane. They can equal a facet normal on
                    // a curved strip and otherwise make that facet prove itself.
                    bool normalSupport = HasWidth(cachedSupport, p);
                    foreach (int vertex in vertices)
                    {
                        // Ignore truly hard neighbors, but a smooth curved neighbor
                        // makes this a boundary point rather than plane evidence.
                        bool pure = incident[vertex].All(f => normals[f] == null || Dot(normals[f], n) < SourceShadeCos || Fits(f));
                        if (pure) support.Add(vertex);
                    }
                    if (!HasWidth(support, p)) continue;
                    // A coarse curved quad can be geometrically coplanar while
                    // its source corner normals still describe a round surface.
                    // Do not turn that angular field into a false plane.
                    double[] field = null, cachedField = null; bool stable = true, cachedStable = cachedFaces != null;
                    foreach (int f in members) for (int k = 0; k < 3; k++)
                    {
                        var candidate = corners[f * 3 + k];
                        if (field == null) field = candidate;
                        else if (Dot(field, candidate) < AnchorCos * Math.Sqrt(Dot(field, field) * Dot(candidate, candidate))) stable = false;
                        if (cachedFaces == null || !cachedFaces[f]) { cachedStable = false; continue; }
                        var cached = mesh.normals[mesh.triangles[f][k]];
                        if (cachedField == null) cachedField = cached;
                        else if (cached == null || Dot(cachedField, cached) < AnchorCos * Math.Sqrt(Dot(cachedField, cachedField) * Dot(cached, cached))) cachedStable = false;
                    }
                    // A thin strip may be coplanar only within coordinate noise.
                    // Its width must resolve a 0.01-degree normal before geometric
                    // anchors can override a varying source normal field.
                    double normalResolution = tolerance / Math.Sin(0.01 * Math.PI / 180);
                    bool resolved = WidthSquared(vertices, p) > normalResolution * normalResolution;
                    // Include boundary corners in the field check. Examining
                    // only the anchors hides a curve's changing edge normals.
                    if (!stable && !cachedStable && (!normalSupport || !resolved)) continue;
                    var planeNormal = n; var planePoint = anchor;
                    if (stable && !resolved && field != null)
                    {
                        var candidate = Unit(field[0], field[1], field[2]);
                        var center = new[] { vertices.Average(i => p[i][0]), vertices.Average(i => p[i][1]), vertices.Average(i => p[i][2]) };
                        // Prefer a coherent source field when it also fits every
                        // position. A noisy skinny triangle is a poor normal estimator.
                        if (candidate != null && vertices.All(i => Math.Abs(Signed(p[i], candidate, center)) <= tolerance))
                        { planeNormal = candidate; planePoint = center; }
                    }
                    int id = planes.Count;
                    planes.Add(new Plane { Normal = (double[])planeNormal.Clone(), Point = (double[])planePoint.Clone() });
                    foreach (int f in members) region[f] = id;
                }
                var first = new Dictionary<long, int>();
                for (int f = 0; f < face.Length; f++) for (int k = 0; k < 3; k++)
                {
                    int a = face[f][k], b = face[f][(k + 1) % 3], previous; long key = EdgeKey(a, b);
                    if (!first.TryGetValue(key, out previous)) first.Add(key, region[f]);
                    else if (previous != region[f] && (previous >= 0 || region[f] >= 0)) boundaryPaths[key] = new[] { a, b };
                }
            }
            static double Signed(double[] p, double[] n, double[] anchor)
            { return (p[0] - anchor[0]) * n[0] + (p[1] - anchor[1]) * n[1] + (p[2] - anchor[2]) * n[2]; }
            bool HasWidth(HashSet<int> support, double[][] p)
            { return WidthSquared(support, p) > tolerance * tolerance * 64; }
            static double WidthSquared(HashSet<int> support, double[][] p)
            {
                if (support.Count < 3) return 0;
                int a = support.First(), b = a; double length = 0;
                foreach (int i in support) { double d = Squared(p[a], p[i]); if (d > length) { length = d; b = i; } }
                if (length == 0) return 0;
                double x = p[b][0] - p[a][0], y = p[b][1] - p[a][1], z = p[b][2] - p[a][2];
                double width = 0;
                foreach (int i in support)
                {
                    double u = p[i][0] - p[a][0], v = p[i][1] - p[a][1], w = p[i][2] - p[a][2];
                    double nx = y * w - z * v, ny = z * u - x * w, nz = x * v - y * u;
                    width = Math.Max(width, (nx * nx + ny * ny + nz * nz) / length);
                }
                return width;
            }
            internal double[] FaceNormal(int f) { return region[f] >= 0 ? planes[region[f]].Normal : null; }
            internal double[] GroupNormal(IEnumerable<int> faces)
            {
                double[] n = null;
                foreach (int f in faces)
                {
                    var candidate = FaceNormal(f); if (candidate == null) continue;
                    if (n == null) n = candidate;
                    else if (Dot(n, candidate) < AnchorCos) return null;
                }
                return n;
            }
            internal void AnchorSourceNormals(int[][] face, List<int>[] incident, double[][] unit, double[][] corners)
            {
                for (int vertex = 0; vertex < incident.Length; vertex++)
                {
                    var ids = incident[vertex].Where(f => region[f] >= 0).Select(f => region[f]).Distinct().ToArray();
                    if (ids.Length == 0) continue;
                    foreach (int f in incident[vertex])
                    {
                        int corner = f * 3 + Array.IndexOf(face[f], vertex);
                        if (region[f] >= 0) { corners[corner] = planes[region[f]].Normal; continue; }
                        double best = double.NegativeInfinity; double[] selected = null;
                        foreach (int id in ids)
                        {
                            var n = planes[id].Normal;
                            if (Dot(n, unit[f]) < SmoothCos) continue;
                            double score = Dot(n, corners[corner]);
                            if (score > best) { best = score; selected = n; }
                        }
                        if (selected != null) corners[corner] = selected;
                    }
                }
            }
            int RegionAt(int vertex, List<int>[] incident, bool[] alive, out bool boundary)
            {
                int first = -1; bool other = false, curve = false;
                foreach (int f in incident[vertex])
                {
                    if (!alive[f]) continue;
                    if (region[f] < 0) curve = true;
                    else if (first < 0) first = region[f];
                    else if (first != region[f]) other = true;
                }
                boundary = first >= 0 && (other || curve); return first;
            }
            HashSet<int> Neighbors(int vertex, int[][] face, List<int>[] incident, bool[] alive)
            {
                var first = new Dictionary<int, int>(); var result = new HashSet<int>();
                foreach (int f in incident[vertex])
                {
                    if (!alive[f]) continue;
                    foreach (int other in face[f])
                    {
                        if (other == vertex) continue;
                        int previous;
                        if (!first.TryGetValue(other, out previous)) first.Add(other, region[f]);
                        else if (previous != region[f] && (previous >= 0 || region[f] >= 0)) result.Add(other);
                    }
                }
                return result;
            }
            static double SegmentDistance(double[] p, double[] a, double[] b)
            {
                double x = b[0] - a[0], y = b[1] - a[1], z = b[2] - a[2], square = x * x + y * y + z * z;
                if (square == 0) return Squared(p, a);
                double t = Math.Max(0, Math.Min(1, ((p[0] - a[0]) * x + (p[1] - a[1]) * y + (p[2] - a[2]) * z) / square));
                double dx = a[0] + t * x - p[0], dy = a[1] + t * y - p[1], dz = a[2] + t * z - p[2];
                return dx * dx + dy * dy + dz * dz;
            }
            internal Choice Choose(double[][] p, int[][] face, List<int>[] incident, bool[] alive, int u, int v, double[] seed)
            {
                bool bu, bv; int pu = RegionAt(u, incident, alive, out bu), pv = RegionAt(v, incident, alive, out bv);
                if (bu && bv)
                {
                    var nu = Neighbors(u, face, incident, alive); var nv = Neighbors(v, face, incident, alive);
                    if (nu.Count != 2 || nv.Count != 2 || !nu.Contains(v) || !nv.Contains(u)) return null;
                    int wu = nu.First(i => i != v), wv = nv.First(i => i != u);
                    if (wu == wv) return null;
                    long uv = EdgeKey(u, v), uw = EdgeKey(u, wu), vw = EdgeKey(v, wv);
                    int[] path, left, right;
                    if (!boundaryPaths.TryGetValue(uv, out path) || !boundaryPaths.TryGetValue(uw, out left) || !boundaryPaths.TryGetValue(vw, out right)) return null;
                    bool keepU = Squared(seed, p[u]) <= Squared(seed, p[v]);
                    var merged = path.Concat(keepU ? right : left).Distinct().ToArray();
                    var point = keepU ? p[u] : p[v]; var end = keepU ? p[wv] : p[wu];
                    if (merged.Any(i => SegmentDistance(sourcePoints[i], point, end) > boundaryTolerance * boundaryTolerance)) return null;
                    var choice = new Choice { Point = point, Remove = new List<long> { uv, vw }, Add = new Dictionary<long, int[]>() };
                    if (keepU) choice.Add.Add(EdgeKey(u, wv), merged);
                    else { choice.Remove.Add(uw); choice.Add.Add(uw, merged); choice.Add.Add(EdgeKey(u, wv), right); }
                    return choice;
                }
                if (bu) return new Choice { Point = p[u] };
                if (bv)
                {
                    var choice = new Choice { Point = p[v], Remove = new List<long>(), Add = new Dictionary<long, int[]>() };
                    foreach (int w in Neighbors(v, face, incident, alive))
                    {
                        long key = EdgeKey(v, w); int[] path;
                        if (!boundaryPaths.TryGetValue(key, out path)) return null;
                        choice.Remove.Add(key); choice.Add.Add(EdgeKey(u, w), path);
                    }
                    return choice;
                }
                int id = pu >= 0 ? pu : pv;
                if (id < 0) return new Choice { Point = seed, Project = true };
                var plane = planes[id]; double signed = Signed(seed, plane.Normal, plane.Point);
                return new Choice { Point = new[] { seed[0] - signed * plane.Normal[0], seed[1] - signed * plane.Normal[1], seed[2] - signed * plane.Normal[2] } };
            }
            internal void Commit(Choice choice)
            {
                if (choice.Remove != null) foreach (long key in choice.Remove) boundaryPaths.Remove(key);
                if (choice.Add != null) foreach (var entry in choice.Add) boundaryPaths[entry.Key] = entry.Value;
            }
        }

        // Closest-point sampling of the original smooth field, accelerated by an
        // AABB hierarchy. Normal compatibility selects the proper side of thin
        // walls and hard corners; a sample is shared by the whole output fan.
        sealed class SourceSurface
        {
            sealed class Node
            {
                internal double[] Min = new double[3], Max = new double[3];
                internal int Start, Count; internal Node Left, Right;
            }
            readonly MeshShape mesh;
            readonly double[][] faceNormals, cornerNormals;
            readonly int[] order;
            readonly double[][] centers;
            readonly int[] vertexComponent, faceComponent;
            readonly Node root;
            internal SourceSurface(MeshShape value, double[][] faces, double[][] corners)
                : this(value, faces, corners, value.triangles, value.vertices.Length) { }
            internal SourceSurface(MeshShape value, double[][] faces, double[][] corners, int[][] topology, int count)
            {
                mesh = value; faceNormals = faces; cornerNormals = corners;
                vertexComponent = Enumerable.Range(0, count).ToArray();
                int Root(int i) { while (vertexComponent[i] != i) { vertexComponent[i] = vertexComponent[vertexComponent[i]]; i = vertexComponent[i]; } return i; }
                foreach (var t in topology) { vertexComponent[Root(t[1])] = Root(t[0]); vertexComponent[Root(t[2])] = Root(t[0]); }
                for (int i = 0; i < count; i++) vertexComponent[i] = Root(i);
                faceComponent = topology.Select(t => vertexComponent[t[0]]).ToArray();
                order = Enumerable.Range(0, mesh.triangles.Length).ToArray();
                centers = new double[3][];
                for (int k = 0; k < 3; k++)
                {
                    centers[k] = new double[order.Length];
                    for (int f = 0; f < order.Length; f++)
                    { var t = mesh.triangles[f]; centers[k][f] = (mesh.vertices[t[0]][k] + mesh.vertices[t[1]][k] + mesh.vertices[t[2]][k]) / 3; }
                }
                root = Build(0, order.Length);
                centers = null; // Needed for construction only; release before QEM.
            }
            Node Build(int start, int count)
            {
                var node = new Node { Start = start, Count = count };
                for (int k = 0; k < 3; k++) { node.Min[k] = double.PositiveInfinity; node.Max[k] = double.NegativeInfinity; }
                for (int i = start; i < start + count; i++) foreach (int index in mesh.triangles[order[i]])
                {
                    var p = mesh.vertices[index];
                    for (int k = 0; k < 3; k++) { node.Min[k] = Math.Min(node.Min[k], p[k]); node.Max[k] = Math.Max(node.Max[k], p[k]); }
                }
                if (count <= 8) return node;
                int axis = 0;
                for (int k = 1; k < 3; k++) if (node.Max[k] - node.Min[k] > node.Max[axis] - node.Min[axis]) axis = k;
                Array.Sort(order, start, count, Comparer<int>.Create((a, b) =>
                { int c = centers[axis][a].CompareTo(centers[axis][b]); return c != 0 ? c : a.CompareTo(b); }));
                int half = count / 2;
                node.Left = Build(start, half); node.Right = Build(start + half, count - half);
                return node;
            }
            static double BoundsDistance(Node node, double[] p)
            {
                double distance = 0;
                for (int k = 0; k < 3; k++)
                { double d = p[k] < node.Min[k] ? node.Min[k] - p[k] : p[k] > node.Max[k] ? p[k] - node.Max[k] : 0; distance += d * d; }
                return distance;
            }
            internal bool Sample(double[] p, double[] direction, out double[] normal, out double[] point)
            { return SampleComponent(p, direction, -1, out normal, out point); }
            internal bool SampleVertex(double[] p, double[] direction, int vertex, out double[] normal, out double[] point)
            { return SampleComponent(p, direction, vertexComponent[vertex], out normal, out point); }
            bool SampleComponent(double[] p, double[] direction, int component, out double[] normal, out double[] point)
            {
                double distance = double.PositiveInfinity; normal = null; point = null;
                Search(root, p, direction, component, false, ref distance, ref normal, ref point);
                if (normal != null && Dot(normal, direction) >= SourceShadeCos) return true;
                // A normal filter must never make the query jump to a farther
                // surface. Only resolve ambiguous hard-edge samples within the
                // output coordinate's float32 rounding radius.
                double radius = Math.Sqrt(Dot(p, p)) * 5.960464477539063e-8;
                distance = Math.Pow(Math.Sqrt(distance) + radius, 2); normal = null; point = null;
                Search(root, p, direction, component, true, ref distance, ref normal, ref point);
                return normal != null;
            }
            void Search(Node node, double[] p, double[] direction, int component, bool compatibleOnly, ref double distance, ref double[] normal, ref double[] point)
            {
                if (BoundsDistance(node, p) > distance) return;
                if (node.Left != null)
                {
                    double left = BoundsDistance(node.Left, p), right = BoundsDistance(node.Right, p);
                    if (left <= right) { Search(node.Left, p, direction, component, compatibleOnly, ref distance, ref normal, ref point); Search(node.Right, p, direction, component, compatibleOnly, ref distance, ref normal, ref point); }
                    else { Search(node.Right, p, direction, component, compatibleOnly, ref distance, ref normal, ref point); Search(node.Left, p, direction, component, compatibleOnly, ref distance, ref normal, ref point); }
                    return;
                }
                for (int i = node.Start; i < node.Start + node.Count; i++)
                {
                    int f = order[i];
                    if (faceNormals[f] == null || (component >= 0 && faceComponent[f] != component)) continue;
                    double v, w; double candidate = Closest(mesh.triangles[f], p, out v, out w);
                    if (candidate > distance) continue;
                    var a = cornerNormals[f * 3]; var b = cornerNormals[f * 3 + 1]; var c = cornerNormals[f * 3 + 2];
                    var n = Unit((1 - v - w) * a[0] + v * b[0] + w * c[0],
                        (1 - v - w) * a[1] + v * b[1] + w * c[1], (1 - v - w) * a[2] + v * b[2] + w * c[2]);
                    if (n == null || (compatibleOnly && Dot(n, direction) < SourceShadeCos)) continue;
                    if (candidate == distance && normal != null && Dot(n, direction) <= Dot(normal, direction)) continue;
                    distance = candidate; normal = n;
                    var t = mesh.triangles[f]; var pa = mesh.vertices[t[0]]; var pb = mesh.vertices[t[1]]; var pc = mesh.vertices[t[2]];
                    point = new[] { (1 - v - w) * pa[0] + v * pb[0] + w * pc[0],
                        (1 - v - w) * pa[1] + v * pb[1] + w * pc[1], (1 - v - w) * pa[2] + v * pb[2] + w * pc[2] };
                }
            }
            double Closest(int[] triangle, double[] p, out double v, out double w)
            {
                var a = mesh.vertices[triangle[0]]; var b = mesh.vertices[triangle[1]]; var c = mesh.vertices[triangle[2]];
                return TriangleDistance(p, a, b, c, out v, out w);
            }
            internal static double TriangleDistance(double[] p, double[] a, double[] b, double[] c, out double v, out double w)
            {
                double x = b[0] - a[0], y = b[1] - a[1], z = b[2] - a[2];
                double u = c[0] - a[0], s = c[1] - a[1], t = c[2] - a[2];
                double nx = y * t - z * s, ny = z * u - x * t, nz = x * s - y * u;
                double px = p[0] - a[0], py = p[1] - a[1], pz = p[2] - a[2];
                double square = nx * nx + ny * ny + nz * nz;
                v = ((py * t - pz * s) * nx + (pz * u - px * t) * ny + (px * s - py * u) * nz) / square;
                w = ((y * pz - z * py) * nx + (z * px - x * pz) * ny + (x * py - y * px) * nz) / square;
                if (v >= 0 && w >= 0 && v + w <= 1)
                { double height = px * nx + py * ny + pz * nz; return height * height / square; }
                double best = double.PositiveInfinity; v = w = 0;
                for (int edge = 0; edge < 3; edge++)
                {
                    var from = edge == 0 ? a : edge == 1 ? b : c; var to = edge == 0 ? b : edge == 1 ? c : a;
                    double dx = to[0] - from[0], dy = to[1] - from[1], dz = to[2] - from[2];
                    double h = Math.Max(0, Math.Min(1, ((p[0] - from[0]) * dx + (p[1] - from[1]) * dy + (p[2] - from[2]) * dz) / (dx * dx + dy * dy + dz * dz)));
                    double ex = from[0] + h * dx - p[0], ey = from[1] + h * dy - p[1], ez = from[2] + h * dz - p[2];
                    double d = ex * ex + ey * ey + ez * ez;
                    if (d >= best) continue;
                    best = d; v = edge == 0 ? h : edge == 1 ? 1 - h : 0; w = edge == 0 ? 0 : edge == 1 ? h : 1 - h;
                }
                return best;
            }
        }
        static void BuildTopology(MeshShape mesh, out double[][] positions, out int[][] faces)
        {
            var ids = new Dictionary<PositionKey, int>();
            var compact = new List<double[]>(mesh.vertices.Length);
            var map = new int[mesh.vertices.Length];
            for (int i = 0; i < map.Length; i++)
            {
                var key = new PositionKey(mesh.vertices[i]); int id;
                if (!ids.TryGetValue(key, out id))
                { id = compact.Count; ids.Add(key, id); compact.Add((double[])mesh.vertices[i].Clone()); }
                map[i] = id;
            }
            positions = compact.ToArray();
            faces = mesh.triangles.Select(t => new[] { map[t[0]], map[t[1]], map[t[2]] }).ToArray();
        }
        static long EdgeKey(int a, int b)
        { return ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b); }
        static void ChangeEdges(Dictionary<long, int> changes, int a, int b, int c, int delta)
        {
            foreach (long key in new[] { EdgeKey(a, b), EdgeKey(b, c), EdgeKey(c, a) })
            { int value; changes.TryGetValue(key, out value); changes[key] = value + delta; }
        }
        static bool TopologyAllows(int[][] faces, bool[] alive, List<int>[] incident, Dictionary<long, int> use,
            int u, int v, List<int> shared, out Dictionary<long, int> changes)
        {
            changes = new Dictionary<long, int>();
            var affected = new HashSet<int>(incident[u]); affected.UnionWith(incident[v]);
            var surviving = new HashSet<FaceKey>();
            foreach (int f in affected)
            {
                if (!alive[f]) continue;
                var t = faces[f]; ChangeEdges(changes, t[0], t[1], t[2], -1);
                if (shared.Contains(f)) continue;
                int a = t[0] == v ? u : t[0], b = t[1] == v ? u : t[1], c = t[2] == v ? u : t[2];
                if (a == b || b == c || a == c || !surviving.Add(new FaceKey(a, b, c))) return false;
                ChangeEdges(changes, a, b, c, 1);
            }
            foreach (var change in changes)
            {
                int before; use.TryGetValue(change.Key, out before); int after = before + change.Value;
                // A contraction cannot open a formerly closed edge or create a
                // non-manifold edge. Existing open boundary edges keep their IDs.
                if (after < 0 || after > 2 || (after == 1 && before != 1)) return false;
            }
            return true;
        }

        // Support sharp feature curves with extra error planes. This biases QEM
        // toward retaining contours without treating shading seams as open rims
        // or rejecting valid long, thin triangles.
        static void AddFeatureQuadrics(double[] quadric, double[][] position, int[][] face, List<int>[] incident,
            Dictionary<long, int> use, double[][] normals, double[] origin)
        {
            const double FeatureCos = 0.8660254037844386; // 30 degrees
            const double Weight = 10.0;
            foreach (var pair in use)
            {
                if (pair.Value != 2) continue;
                int a = (int)(pair.Key >> 32), b = (int)(pair.Key & 0xffffffff);
                double[] n0 = null, n1 = null;
                foreach (int f in incident[a])
                {
                    var t = face[f]; if (t[0] != b && t[1] != b && t[2] != b) continue;
                    if (n0 == null) n0 = normals[f]; else { n1 = normals[f]; break; }
                }
                if (n0 == null || n1 == null || n0[0] * n1[0] + n0[1] * n1[1] + n0[2] * n1[2] >= FeatureCos) continue;
                var pa = position[a]; var pb = position[b];
                double dx = pb[0] - pa[0], dy = pb[1] - pa[1], dz = pb[2] - pa[2];
                foreach (var normal in new[] { n0, n1 })
                {
                    double nx = dy * normal[2] - dz * normal[1], ny = dz * normal[0] - dx * normal[2], nz = dx * normal[1] - dy * normal[0];
                    double length = Math.Sqrt(nx * nx + ny * ny + nz * nz); if (length == 0) continue;
                    nx /= length; ny /= length; nz /= length;
                    double d = -(nx * (pa[0] - origin[0]) + ny * (pa[1] - origin[1]) + nz * (pa[2] - origin[2]));
                    var values = new[] { nx * nx, nx * ny, nx * nz, nx * d, ny * ny, ny * nz, ny * d, nz * nz, nz * d, d * d };
                    foreach (int v in new[] { a, b }) for (int k = 0; k < 10; k++) quadric[v * 10 + k] += Weight * values[k];
                }
            }
        }

        // Spend more of the same triangle budget on small-radius fillets and
        // bosses. Normal change along an edge measures curvature without making
        // a long, narrow source triangle artificially important by its area.
        static void AddCurvatureQuadrics(double[] quadric, double[][] p, int[][] face, double[][] normals,
            double[][] corners, double[] origin)
        {
            double diagonal = 0;
            for (int k = 0; k < 3; k++) { double span = p.Max(v => v[k]) - p.Min(v => v[k]); diagonal += span * span; }
            double scaleSquared = diagonal * 0.0004; // 2% of the model diagonal
            for (int f = 0; f < face.Length; f++)
            {
                if (normals[f] == null) continue;
                var t = face[f]; double curvatureSquared = 0;
                for (int k = 0; k < 3; k++)
                {
                    int next = (k + 1) % 3; var a = p[t[k]]; var b = p[t[next]];
                    double length = 0, change = 0;
                    for (int j = 0; j < 3; j++)
                    { double d = a[j] - b[j], n = corners[f * 3 + k][j] - corners[f * 3 + next][j]; length += d * d; change += n * n; }
                    if (length > 0) curvatureSquared = Math.Max(curvatureSquared, change / length);
                }
                double weight = Math.Min(16.0, scaleSquared * curvatureSquared);
                if (weight == 0) continue;
                var n0 = normals[f]; var point = p[t[0]];
                double x = n0[0], y = n0[1], z = n0[2];
                double d0 = -(x * (point[0] - origin[0]) + y * (point[1] - origin[1]) + z * (point[2] - origin[2]));
                var values = new[] { x * x, x * y, x * z, x * d0, y * y, y * z, y * d0, z * z, z * d0, d0 * d0 };
                foreach (int v in t) for (int k = 0; k < 10; k++) quadric[v * 10 + k] += weight * values[k];
            }
        }

        // 塌陷成本与最优落点：边界边保持低端点（边界不收缩）；其余边把落点
        // 约束在塌陷边段上，沿段对二次型取最小（三个采样点确定抛物线，顶点
        // 截断回 [0,1]）。无约束最优解在近平面区域会沿曲面漂出邻域——邻面被
        // 拉走后原位成片露白，段内约束以少量可控误差彻底杜绝漂移。
        static double EdgeCost(double[] quadric, double[][] position, double[] scratch, int a, int b, bool[] rim, double[] origin, out double[] placement)
        {
            int oa = 10 * a, ob = 10 * b;
            for (int k = 0; k < 10; k++) scratch[k] = quadric[oa + k] + quadric[ob + k];
            if (rim[a] && rim[b]) { placement = position[a]; return Evaluate(scratch, position[a], origin); }
            double c0 = Evaluate(scratch, position[a], origin);
            double c1 = Evaluate(scratch, position[b], origin);
            var mid = new[] { (position[a][0] + position[b][0]) * 0.5, (position[a][1] + position[b][1]) * 0.5, (position[a][2] + position[b][2]) * 0.5 };
            double half = Evaluate(scratch, mid, origin);
            double bigA = 2 * (c1 - 2 * half + c0);
            double bigB = c1 - c0 - bigA;
            double t = bigA > 0 ? Math.Max(0.0, Math.Min(1.0, -bigB / (2 * bigA))) : (c1 < c0 ? 1.0 : 0.0);
            double interior = bigA * t * t + bigB * t + c0;
            if (interior < c0 && interior < c1)
            {
                placement = new[]
                {
                    position[a][0] + t * (position[b][0] - position[a][0]),
                    position[a][1] + t * (position[b][1] - position[a][1]),
                    position[a][2] + t * (position[b][2] - position[a][2])
                };
                return interior;
            }
            if (c1 < c0) { placement = (double[])position[b].Clone(); return c1; }
            placement = (double[])position[a].Clone();
            return c0;
        }

        static double Evaluate(double[] q, double[] p, double[] origin)
        {
            double x = p[0] - origin[0], y = p[1] - origin[1], z = p[2] - origin[2];
            return q[0] * x * x + 2 * q[1] * x * y + 2 * q[2] * x * z + 2 * q[3] * x
                 + q[4] * y * y + 2 * q[5] * y * z + 2 * q[6] * y
                 + q[7] * z * z + 2 * q[8] * z
                 + q[9];
        }

        // Normal of the triangle with one vertex index optionally placed at another point.
        static double[] NormalAt(double[][] p, int[] t, int movedIndex, double[] moved)
        {
            double[] a = t[0] == movedIndex ? moved : p[t[0]];
            double[] b = t[1] == movedIndex ? moved : p[t[1]];
            double[] c = t[2] == movedIndex ? moved : p[t[2]];
            double x = (b[1] - a[1]) * (c[2] - a[2]) - (b[2] - a[2]) * (c[1] - a[1]);
            double y = (b[2] - a[2]) * (c[0] - a[0]) - (b[0] - a[0]) * (c[2] - a[2]);
            double z = (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]);
            double length = Math.Sqrt(x * x + y * y + z * z);
            return length == 0 ? null : new[] { x / length, y / length, z / length };
        }
    }
}
