using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Runtime.Serialization;
using DLAT.JTReader;
using Reader = DLAT.JTReader.StreamReader;

// Standalone .NET Framework worker. PS objects never enter this process.
partial class Program
{
    sealed class Segment { public Guid Id; public int Ordinal, Type; public byte[] Data; public Element Legacy; }
    sealed class Node { public string Type, Text; public int Id; public int[] Attributes = new int[0], Children = new int[0]; public Guid Segment; public double[] Matrix, RGBA; public uint Inhibit, Final; public byte State; }
    sealed class Binding { public Segment Segment; public double[] Matrix, RGBA; public bool Mirrored; }
    sealed class Face { public int[] Ids; public byte[] RGB; public byte Alpha; public int Surface; public double[] Normal; }
    static int sourceMajor, sourceMinor, defaultMaterialShapes;
    static Dictionary<int, Node> nodes = new Dictionary<int, Node>();
    static Dictionary<Guid, Segment> segments = new Dictionary<Guid, Segment>();
    static List<Binding> bindings = new List<Binding>();
    static readonly HashSet<Guid> alternateTriangleSegments = new HashSet<Guid>();
    static readonly HashSet<int> alternateNodes = new HashSet<int>();
    static List<double[]> vertices = new List<double[]>(); static List<Face> faces = new List<Face>();
    static T Blank<T>() { return (T)FormatterServices.GetUninitializedObject(typeof(T)); }
    static BinaryReader R(byte[] b) { return new BinaryReader(new MemoryStream(b)); }
    static byte[] Bytes(BinaryReader r, int n) { if (n < 0 || n > r.BaseStream.Length - r.BaseStream.Position) throw new InvalidDataException("Truncated JT data"); return r.ReadBytes(n); }
    static Guid GuidAt(BinaryReader r) { return new Guid(Bytes(r, 16)); }
    static int Count(BinaryReader reader, int minBytesPerEntry)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > (reader.BaseStream.Length - reader.BaseStream.Position) / minBytesPerEntry)
            throw new InvalidDataException("Invalid JT count");
        return count;
    }
    static double[] Identity() { var m = new double[16]; for (int i = 0; i < 16; i += 5) m[i] = 1; return m; }
    static double[] Multiply(double[] a, double[] b) { var c = new double[16]; for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) for (int k = 0; k < 4; k++) c[i * 4 + j] += a[i * 4 + k] * b[k * 4 + j]; return c; }
    static double[] Transform(double[] p, double[] m, bool point) { return Enumerable.Range(0, 3).Select(k => p[0] * m[k] + p[1] * m[4 + k] + p[2] * m[8 + k] + (point ? m[12 + k] : 0)).ToArray(); }
    static double[] Normalize(double[] p) { double l = Math.Sqrt(p.Sum(v => v * v)); return l > 1e-12 ? p.Select(v => v / l).ToArray() : new double[3]; }
    static double[] Cross(double[] a, double[] b, double[] c) { double x = b[0] - a[0], y = b[1] - a[1], z = b[2] - a[2], u = c[0] - a[0], v = c[1] - a[1], w = c[2] - a[2]; return new[] { y * w - z * v, z * u - x * w, x * v - y * u }; }
    static bool IsMirroredRigidTransform(double[] matrix)
    {
        if (matrix.Any(v => double.IsNaN(v) || double.IsInfinity(v))
            || Math.Abs(matrix[3]) + Math.Abs(matrix[7]) + Math.Abs(matrix[11]) + Math.Abs(matrix[15] - 1) > 1e-8)
            throw new InvalidDataException("Invalid affine transform");
        for (int i = 0; i < 3; i++)
        for (int j = 0; j < 3; j++)
        {
            double dot = 0;
            for (int k = 0; k < 3; k++) dot += matrix[4 * i + k] * matrix[4 * j + k];
            if (Math.Abs(dot - (i == j ? 1 : 0)) > 1e-5)
                throw new InvalidDataException("Only orthogonal JT transforms supported");
        }
        double determinant = matrix[0] * (matrix[5] * matrix[10] - matrix[6] * matrix[9])
            - matrix[1] * (matrix[4] * matrix[10] - matrix[6] * matrix[8])
            + matrix[2] * (matrix[4] * matrix[9] - matrix[5] * matrix[8]);
        return determinant < 0;
    }

    static void ReverseTriangle(int[] indices)
    {
        int second = indices[1];
        indices[1] = indices[2];
        indices[2] = second;
    }

    static void ApplyVertexColor(Face face, IList<double> colors, int components, int a, int b, int c)
    {
        int count = colors.Count / components;
        if ((uint)a >= (uint)count || (uint)b >= (uint)count || (uint)c >= (uint)count)
            throw new InvalidDataException("Invalid JT triangle corner colour indices " + a + "," + b + "," + c
                + " (count " + count + ") at shape " + face.Surface);

        // CGR stores one colour per triangle. The centroid interpolant retains
        // the exact source colour when all three JT corners have the same value.
        face.RGB = new byte[3];
        for (int i = 0; i < components; i++)
        {
            double value = (colors[a * components + i] + colors[b * components + i] + colors[c * components + i]) / 3;
            byte channel = (byte)Math.Round(value * 255);
            if (i == 3) face.Alpha = channel;
            else face.RGB[i] = channel;
        }
    }
    static string Type(Guid g) { return g.ToString().Substring(0, 8); }
    static readonly Guid WrappedAttributeType = new Guid("96603dd3-5a0f-40f5-97cb-1e9647cbd37e");
    static void ParseElement(BinaryReader r)
    {
        long offset = r.BaseStream.Position;
        int length = r.ReadInt32(); if (length < 21) throw new InvalidDataException("Invalid element"); long end = r.BaseStream.Position + length; if (end > r.BaseStream.Length) throw new InvalidDataException("Truncated element");
        Guid elementType = GuidAt(r);
        byte baseType = r.ReadByte();
        int id = r.ReadInt32();
        if (baseType == 9 && elementType == WrappedAttributeType)
        {
            if (end - r.BaseStream.Position < 21) throw new InvalidDataException("Truncated wrapped JT attribute at LSG offset 0x" + offset.ToString("X"));
            // The outer field can repeat. Scene nodes reference the ID in the embedded element header.
            elementType = GuidAt(r);
            baseType = r.ReadByte();
            id = r.ReadInt32();
            if (baseType != 3) throw new NotSupportedException("Unsupported JT wrapped attribute base type " + baseType);
        }
        string type = Type(elementType);
        var body = Bytes(r, (int)(end - r.BaseStream.Position)); var n = new Node { Id = id, Type = type }; using (var b = R(body))
        {
            if (baseType <= 2) { b.ReadByte(); b.ReadUInt32(); int count = Count(b, 4); n.Attributes = Enumerable.Range(0, count).Select(_ => b.ReadInt32()).ToArray(); }
            if (new[] { "10dd103e", "10dd101b", "10dd102c", "10dd104c", "10dd10f3", "ce357245", "ce357244" }.Contains(type)) { b.ReadByte(); int count = Count(b, 4); n.Children = Enumerable.Range(0, count).Select(_ => b.ReadInt32()).ToArray(); }
            else if (type == "10dd102a") { b.ReadByte(); n.Children = new[] { b.ReadInt32() }; }
            else if (type == "10dd1083") { b.BaseStream.Position = 10; b.ReadByte(); ushort mask = b.ReadUInt16(); int count = Enumerable.Range(0, 16).Count(i => (mask & (0x8000 >> i)) != 0); bool wide = body.Length - 13 > count * 6; n.Matrix = Identity(); for (int i = 0; i < 16; i++) if ((mask & (0x8000 >> i)) != 0) n.Matrix[i] = wide ? b.ReadDouble() : b.ReadSingle(); }
            else if (type == "10dd1030") { if (body.Length < 11 || body[10] != 1 || (body[0] != 1 && body[0] != 2) || body.Length != (body[0] == 2 ? 93 : 89)) throw new NotSupportedException("Unsupported material layout at node " + id); b.BaseStream.Position = 1; n.State = b.ReadByte(); n.Inhibit = b.ReadUInt32(); n.Final = b.ReadUInt32(); b.BaseStream.Position = 29; n.RGBA = Enumerable.Range(0, 4).Select(_ => (double)b.ReadSingle()).ToArray(); if (n.RGBA.Any(v => double.IsNaN(v) || v < 0 || v > 1)) throw new InvalidDataException("Invalid RGBA"); }
            else if (type == "10dd106e")
            {
                b.BaseStream.Position = 6;
                int count = b.ReadInt32();
                if (count < 0) throw new InvalidDataException("Invalid JT string length");
                int lengthBytes = (int)Math.Min(2L * count, b.BaseStream.Length - b.BaseStream.Position);
                n.Text = Encoding.Unicode.GetString(Bytes(b, lengthBytes)).TrimEnd('\0');
            }
            else if (baseType == 8) { b.BaseStream.Position = 6; n.Segment = GuidAt(b); }
        }
        Node previous;
        if (nodes.TryGetValue(id, out previous))
            throw new InvalidDataException("Duplicate JT object ID " + id + " (" + previous.Type + ", " + type + ") at LSG offset 0x" + offset.ToString("X"));
        nodes.Add(id, n);
    }
    static void ParseLSG(byte[] data)
    {
        using (var r = R(data))
        {
            for (int seq = 0; seq < 2; seq++) { while (true) { long p = r.BaseStream.Position; int len = r.ReadInt32(); Guid g = GuidAt(r); r.BaseStream.Position = p; if (g == new Guid("ffffffff-ffff-ffff-ffff-ffffffffffff")) { if (len != 16) throw new InvalidDataException("Invalid end marker"); r.BaseStream.Position = p + 20; break; } ParseElement(r); } }
            if (r.ReadUInt16() != 1) throw new InvalidDataException("Property table version"); int count = Count(r, 8); for (int i = 0; i < count; i++) { int id = r.ReadInt32(); while (true) { int key = r.ReadInt32(); if (key == 0) break; int value = r.ReadInt32(); if (nodes[key].Text == "JT_LLPROP_SHAPEIMPL") nodes[id].Segment = nodes[value].Segment; } }
            if (r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("Trailing LSG bytes");
        }
    }
    // Segment type 6 can carry triangle shapes for several alternatives,
    // so its type alone cannot identify which child to export.
    static bool TriangleSegment(Segment s) { return s.Data != null && (s.Type == 7 || (sourceMajor <= 9 && s.Type == 6)); }
    static bool HasTriangle(int id, bool requireLoaded, HashSet<int> path)
    {
        if (path.Count > 1000 || !path.Add(id)) throw new InvalidDataException("Cyclic JT LOD graph");
        Node n;
        if (!nodes.TryGetValue(id, out n)) throw new InvalidDataException("Missing JT LOD node: " + id);
        bool found = false;
        if (n.Type == "10dd1077")
        {
            Segment s;
            if (n.Segment == Guid.Empty || !segments.TryGetValue(n.Segment, out s))
                throw new InvalidDataException("Missing shape binding in LOD node: " + id);
            found = !requireLoaded || TriangleSegment(s);
        }
        if (!found) found = n.Children.Any(child => HasTriangle(child, requireLoaded, path));
        path.Remove(id);
        return found;
    }

    static void MarkAlternateTriangles(int id, int depth)
    {
        if (depth > 1000) throw new InvalidDataException("JT LOD graph is too deep");
        if (!alternateNodes.Add(id)) return;
        Node node;
        if (!nodes.TryGetValue(id, out node)) throw new InvalidDataException("Missing JT LOD node: " + id);
        if (node.Type == "10dd1077")
        {
            Segment segment;
            if (segments.TryGetValue(node.Segment, out segment) && TriangleSegment(segment))
                alternateTriangleSegments.Add(segment.Id);
        }
        foreach (int child in node.Children) MarkAlternateTriangles(child, depth + 1);
    }

    static void Walk(int id, double[] matrix, double[] rgba, uint final, HashSet<int> path)
    {
        if (path.Count > 1000 || !path.Add(id)) throw new InvalidDataException("Cyclic JT graph"); var n = nodes[id]; rgba = (double[])rgba.Clone();
        foreach (int attr in n.Attributes) { var a = nodes[attr]; if (a.RGBA != null && (a.State & 4) == 0) { uint allowed = ~a.Inhibit & ((a.State & 2) != 0 ? uint.MaxValue : ~final); if ((allowed & 64) != 0) Array.Copy(a.RGBA, rgba, 3); if ((allowed & 128) != 0) rgba[3] = a.RGBA[3]; final |= a.Final & allowed; } if (a.Matrix != null) { if (sourceMajor > 9 && n.Type != "10dd102a") throw new InvalidDataException("Transform on non-instance node"); matrix = Multiply(a.Matrix, matrix); } }
        if (n.Type == "10dd1077") { Segment s; if (n.Segment == Guid.Empty || !segments.TryGetValue(n.Segment, out s)) throw new InvalidDataException("Missing shape binding"); if (rgba.Take(3).Any(double.IsNaN)) { for(int i=0;i<3;i++)if(double.IsNaN(rgba[i]))rgba[i]=0.8; defaultMaterialShapes++; Console.WriteLine("MATERIAL_DEFAULT node="+id+" RGB=204,204,204; application neutral default for undefined diffuse RGB"); } bindings.Add(new Binding { Segment = s, Matrix = matrix, RGBA = rgba, Mirrored = IsMirroredRigidTransform(matrix) }); }
        var selectedChildren = n.Children;
        if (n.Type == "10dd104c" && n.Children.Length > 1)
        {
            int closest = Array.FindIndex(n.Children, child => HasTriangle(child, true, new HashSet<int>()));
            if (closest < 0 && HasTriangle(id, false, new HashSet<int>()))
                throw new NotSupportedException("No supported highest-detail triangle LOD at node " + id);
            selectedChildren = closest < 0 ? new int[0] : new[] { n.Children[closest] };
            for (int i = 0; i < n.Children.Length; i++)
                if (i != closest) MarkAlternateTriangles(n.Children[i], 0);
            Console.WriteLine("LOD node=" + id + " alternatives=" + n.Children.Length +
                " selected=" + (closest < 0 ? "non-triangle" : n.Children[closest] + " index=" + closest));
        }
        foreach (int child in selectedChildren) Walk(child, matrix, rgba, final, path);
        path.Remove(id);
    }
    static void Decode(Binding binding, int surface, JTFile file)
    {
        var body = binding.Segment.Data; var reader = new Reader(new MemoryStream(body), file); reader.Position = 41;
        // Read topology separately so unsupported vertex layouts fail before decoding.
        var deg = new List<int[]>(); for (int i = 0; i < 8; i++) deg.Add(Int32CDP.ReadVecI32(reader, PredictorType.PredNull).ToArray());
        var val = Int32CDP.ReadVecI32(reader, PredictorType.PredNull); var groups = Int32CDP.ReadVecI32(reader, PredictorType.PredNull); var flags = Int32CDP.ReadVecI32(reader, PredictorType.PredLag1);
        var masks = new List<int[]>();
        for (int i = 0; i < 8; i++) masks.Add(Int32CDP.ReadVecI32(reader, PredictorType.PredNull).ToArray());
        var next = Int32CDP.ReadVecI32(reader, PredictorType.PredNull);
        var high = reader.ReadVecU32().Select(v => (long)v).ToArray();
        var splits = Int32CDP.ReadVecI32(reader, PredictorType.PredLag1);
        var positions = Int32CDP.ReadVecI32(reader, PredictorType.PredNull);
        reader.ReadU32();
        long vertexPos = reader.Position;
        ulong vertexBindings = reader.ReadU64();
        // XYZ with optional normals, RGB(A), and vertex flags.
        if ((vertexBindings & ~0x78UL) != 0x2 || (vertexBindings & 0x30UL) == 0x30)
            throw new NotSupportedException("Unsupported JT vertex bindings 0x" + vertexBindings.ToString("X"));
        if (new QuantizationParameters(reader).bitsPerVertex != 0)
            throw new NotSupportedException("Quantized coordinates unsupported");
        int topologicalVertices = reader.ReadI32();
        if (topologicalVertices == 0 && val.Count == 0 && deg.All(values => values.Length == 0))
        {
            Console.WriteLine("EMPTY_TRIANGLE_SHAPE surface=" + surface);
            return;
        }
        if (topologicalVertices <= 0) throw new InvalidDataException("Missing JT topological vertices");
        reader.Position = vertexPos;
        var records = new TopologicallyCompressedVertexRecords(reader);
        var xyz = records.compressedVertexCoordinateArray?.vertexCoordinates;
        var normals = records.compressedVertexNormalArray?.normalCoordinates;
        var colors = records.compressedVertexColorArray;
        if (colors != null && colors.Components != vertexBindings.Color())
            throw new InvalidDataException("JT colour binding and component count disagree at shape " + surface);
        if (xyz == null || xyz.Count == 0 || xyz.Count % 3 != 0
            || ((vertexBindings & 0x8) != 0 && (normals == null || normals.Count % 3 != 0)))
            throw new InvalidDataException("Invalid JT vertex or normal array");
        if (normals == null) Console.WriteLine("GEOMETRIC_NORMALS surface=" + surface);
        var driver = new MeshCoderDriver();
        driver.setInputData(val, deg, groups, flags, masks, next, null, high, splits, positions);
        driver.decode();
        var mesh = driver.DecodedMesh;
        int offset = vertices.Count;
        var local = new List<double[]>();
        for (int i = 0; i < xyz.Count; i += 3)
            local.Add(Transform(new[] { (double)xyz[i], xyz[i + 1], xyz[i + 2] }, binding.Matrix, true));
        vertices.AddRange(local);
        byte[] rgb = binding.RGBA.Take(3).Select(v => (byte)Math.Round(v * 255)).ToArray();
        int correctedWinding = 0, hiddenTriangles = 0;
        for (int f = 0; f < mesh.numFaces(); f++)
        {
            int nv = mesh.faceNumVts(f);
            if (mesh.faceGrp(f) < 0) { hiddenTriangles += Math.Max(0, nv - 2); continue; }
            for (int k = 1; k < nv - 1; k++)
            {
                int[] ids = { mesh.faceVtx(f, 0), mesh.faceVtx(f, k), mesh.faceVtx(f, k + 1) };
                if (ids.Any(i => i < 0 || i >= local.Count)) throw new InvalidDataException("Invalid vertex index");
                if (binding.Mirrored) ReverseTriangle(ids);
                var geo = Cross(local[ids[0]], local[ids[1]], local[ids[2]]);
                var original = ids.Select(i => new[] { (double)xyz[i * 3], xyz[i * 3 + 1], xyz[i * 3 + 2] }).ToArray();
                var sourceGeo = Cross(original[0], original[1], original[2]);
                if (Math.Sqrt(sourceGeo.Sum(v => v * v)) <= 1e-12) continue;
                var sum = new double[3];
                if (normals != null)
                {
                    foreach (int i in ids)
                    {
                        int ni = mesh.faceVtxAttr(f, i);
                        if (ni < 0) continue;
                        if (ni >= normals.Count / 3) throw new InvalidDataException("Invalid normal index");
                        var norm = Normalize(Transform(new[] { (double)normals[ni * 3], normals[ni * 3 + 1],
                            normals[ni * 3 + 2] }, binding.Matrix, false));
                        for (int j = 0; j < 3; j++) sum[j] += norm[j];
                    }
                    sum = Normalize(sum);
                    if (Enumerable.Range(0, 3).Sum(j => sum[j] * geo[j]) < 0)
                    {
                        ReverseTriangle(ids);
                        correctedWinding++;
                    }
                }
                if (sum.Sum(v => v * v) < 0.5) sum = Normalize(geo);
                var face = new Face { Ids = ids.Select(i => i + offset).ToArray(), RGB = rgb,
                    Alpha = (byte)Math.Round(binding.RGBA[3] * 255), Surface = surface, Normal = sum };
                if (colors != null)
                    ApplyVertexColor(face, colors.colorValues, colors.Components, mesh.faceVtxAttr(f, ids[0], false),
                        mesh.faceVtxAttr(f, ids[1], false), mesh.faceVtxAttr(f, ids[2], false));
                faces.Add(face);
            }
        }
        Console.WriteLine("Shape " + surface + " vertices=" + local.Count + " totalFaces=" + faces.Count + " sourceNormalWindingCorrections=" + correctedWinding + " hiddenTopologyTriangles=" + hiddenTriangles);
    }
    static Guid LegacyGuid(GUID id) { return Guid.ParseExact(id.ToString().Replace("-", ""), "N"); }
    static void ParseLegacyFile(string input)
    {
        // JT 8/9 use 32-bit TOC offsets. Element IDs, LSG attributes and
        // compression are read by the version-aware reader.
        var file = new JTFile(input, triangleGeometryOnly: true);
        int ordinal = 0;
        foreach (var segment in file.segments.Values)
        {
            var result = new Segment { Id=LegacyGuid(segment.segmentID), Type=segment.segmentType, Ordinal=ordinal++ };
            if (segment.segmentType == 7 || segment.segmentType == 6)
            {
                var lods=segment.elements.Where(e=>e.elementType==typeof(Tri_StripSetShapeLODData)).ToArray();
                if(lods.Length>1)throw new NotSupportedException("Multiple legacy JT highest-detail payloads");
                if(lods.Length==1){result.Legacy=lods[0];result.Data=new byte[0];}
            }
            segments.Add(result.Id,result);
        }
        foreach (var entry in file.elements)
        {
            var element=entry.Value;
            var n=new Node { Id=entry.Key, Type=element.objectTypeID.ToString().Substring(0,8).ToLowerInvariant() };
            var data=element.elementData;
            var node=data as BaseNodeData;
            if(node!=null)n.Attributes=(int[])node.attributeObjectID.Clone();
            var group=data as GroupNodeData;
            if(group!=null)n.Children=(int[])group.childNodeObjectID.Clone();
            var instance=data as InstanceNodeData;
            if(instance!=null)n.Children=new[]{instance.childNodeObjectID};
            var transform=data as GeometricTransformAttributeData;
            if(transform!=null)n.Matrix=(double[])transform.matrix.raw.Clone();
            var material=data as MaterialAttributeData;
            if(material!=null)
            {
                n.State=material.stateFlags;
                // JT8 material diffuse RGB and alpha share field-inhibit bit 1.
                n.Inhibit=(material.fieldInhibitFlags&2)!=0?192U:0U;
                n.Final=(material.stateFlags&1)!=0?192U:0U;
                var color=material.diffuseColor;
                n.RGBA=new[]{(double)color.r,color.g,color.b,color.a};
                if(n.RGBA.Take(3).All(v=>v==-1)) { n.Inhibit|=64; for(int k=0;k<3;k++)n.RGBA[k]=0; Console.WriteLine("MATERIAL_UNDEFINED node="+n.Id+" RGB=-1,-1,-1; inherit diffuse RGB"); }
                if(n.RGBA.Any(v=>double.IsNaN(v)||double.IsInfinity(v)||v<0||v>1))throw new InvalidDataException("Invalid JT8 RGBA node="+n.Id+" flags="+material.dataFlags+" values="+string.Join(",",n.RGBA));
            }
            var text=data as StringPropertyAtomData;
            if(text!=null)n.Text=text.value;
            var late=data as LateLoadedPropertyAtomData;
            if(late!=null)n.Segment=LegacyGuid(late.segmeentID);
            nodes.Add(n.Id,n);
        }
        if(file.propertyTable.versionNumber!=1)throw new NotSupportedException("JT8 property table version");
        foreach(var table in file.propertyTable.nodePropertyTable)
            foreach(var property in table.Value.propertyAtomObjectID)
                if(nodes[property.Key].Text=="JT_LLPROP_SHAPEIMPL")nodes[table.Key].Segment=nodes[property.Value].Segment;
        Console.WriteLine("JT_LEGACY_GRAPH major="+sourceMajor+" segments="+segments.Count+" types="+string.Join(",",segments.Values.GroupBy(s=>s.Type).Select(g=>g.Key+":"+g.Count()))+" nodes="+nodes.Count+" partitions="+nodes.Values.Count(n=>n.Type=="10dd103e"));
        if(sourceMajor==9)Console.WriteLine("JT9_NODE_TYPES "+string.Join(",",nodes.Values.GroupBy(n=>n.Type).Select(g=>g.Key+":"+g.Count())));
    }
    static void DecodeLegacy(Binding binding,int surface)
    {
        var lod=(Tri_StripSetShapeLODData)binding.Segment.Legacy.elementData;
        var rep=lod.vertexBasedShapeData;
        if(rep==null || rep.parameters.bitsPerVertex!=0 || rep.colorBinding>1 || rep.textureCoordBinding!=0 || rep.normalBinding>1)
            throw new NotSupportedException("Unsupported JT8 strip vertex layout at shape "+surface);
        var xyz=rep.GetVertices();var normals=rep.GetNormals();var strips=rep.indices;
        var colors = rep.colorBinding == VertexBasedShapeCompressedRepData.BINDING_PER_VERTEX ? rep.GetColors().ConvertAll(value => (double)value) : null;
        if (colors != null && (colors.Count != xyz.Count || colors.Any(value => double.IsNaN(value) || value < 0 || value > 1)))
            throw new InvalidDataException("Invalid JT8 vertex colours at shape " + surface);
        if(xyz.Count%3!=0 || (rep.normalBinding==1 && normals.Count!=xyz.Count) || strips.Count<2 || strips[0]!=0 || strips[strips.Count-1]!=xyz.Count/3)
            throw new InvalidDataException("Invalid JT8 strip arrays");
        for(int i=1;i<strips.Count;i++)if(strips[i]<strips[i-1])throw new InvalidDataException("Unordered JT8 strip boundaries");
        var local=new List<double[]>();int offset=vertices.Count;
        for(int i=0;i<xyz.Count;i+=3)local.Add(Transform(new[]{(double)xyz[i],xyz[i+1],xyz[i+2]},binding.Matrix,true));
        vertices.AddRange(local);
        var rgb=binding.RGBA.Take(3).Select(v=>(byte)Math.Round(v*255)).ToArray();
        int corrected=0,degenerate=0,triangles=0;
        for(int strip=1;strip<strips.Count;strip++)for(int j=strips[strip-1];j<strips[strip]-2;j++)
        {
            triangles++;int[] ids=(j-strips[strip-1])%2==0?new[]{j,j+1,j+2}:new[]{j+1,j,j+2};
            if (binding.Mirrored) ReverseTriangle(ids);
            var original=ids.Select(i=>new[]{(double)xyz[3*i],xyz[3*i+1],xyz[3*i+2]}).ToArray();
            var sourceGeo=Cross(original[0],original[1],original[2]);
            if(Math.Sqrt(sourceGeo.Sum(v=>v*v))<=1e-12){degenerate++;continue;}
            var geo=Cross(local[ids[0]],local[ids[1]],local[ids[2]]);var sum=new double[3];
            if(rep.normalBinding==1)foreach(int i in ids)
            {
                var normal=Normalize(Transform(new[]{(double)normals[3*i],normals[3*i+1],normals[3*i+2]},binding.Matrix,false));
                for(int k=0;k<3;k++)sum[k]+=normal[k];
            }
            sum=Normalize(sum);
            if(Enumerable.Range(0,3).Sum(k=>sum[k]*geo[k])<0){int temp=ids[1];ids[1]=ids[2];ids[2]=temp;corrected++;}
            if(sum.Sum(v=>v*v)<0.5)sum=Normalize(geo);
            var face = new Face { Ids=ids.Select(i=>i+offset).ToArray(), RGB=rgb, Alpha=(byte)Math.Round(binding.RGBA[3]*255), Surface=surface, Normal=sum };
            if (colors != null) ApplyVertexColor(face, colors, 3, ids[0], ids[1], ids[2]);
            faces.Add(face);
        }
        Console.WriteLine("JT8_STRIPS shape="+surface+" strips="+(strips.Count-1)+" triangles="+triangles+" degenerate="+degenerate+" sourceNormalWindingCorrections="+corrected);
    }
    static float F(double v) { float f = (float)v; if (float.IsNaN(f) || float.IsInfinity(f)) throw new InvalidDataException("Nonfinite mesh"); return f; }
    static void ValidateHeader(BinaryReader r,string input)
    {
        string header=Encoding.ASCII.GetString(Bytes(r,80)).TrimEnd('\0',' ','\r','\n');
        byte order=r.ReadByte();
        Console.WriteLine("DECODER=TxTools.JtDecoder rgba-v10");
        Console.WriteLine("INPUT="+input);
        Console.WriteLine("JT_HEADER="+header+" BYTE_ORDER="+order);
        var version=System.Text.RegularExpressions.Regex.Match(header,@"^Version\s+(\d+)\.(\d+)\s+JT(?:\s|$)");
        if(!version.Success)throw new InvalidDataException("Invalid JT header: "+header);
        int major=int.Parse(version.Groups[1].Value),minor=int.Parse(version.Groups[2].Value); sourceMajor=major; sourceMinor=minor;
        if(order>1)throw new InvalidDataException("Invalid JT byte-order marker: "+order);
        bool supportedVersion = (major == 8 && (minor == 0 || minor == 1 || minor == 2))
            || (major == 9 && (minor == 0 || minor == 1 || minor == 4 || minor == 5))
            || (major == 10 && (minor == 0 || minor == 1 || minor == 2 || minor == 6));
        if (order != 0 || !supportedVersion)
            throw new NotSupportedException("Direct decoder does not support JT "+major+"."+minor+
                " byteOrder="+order+"; use the loaded PS native representation. Source: "+input);
    }
    static void ParseModernFile(byte[] bytes)
    {
        using (var r = R(bytes))
        {
            r.BaseStream.Position = 85;
            long toc = r.ReadInt64();
            if (toc < 0 || toc > bytes.LongLength - 4) throw new InvalidDataException("Invalid JT table offset");
            r.BaseStream.Position = toc;
            int count = Count(r, 32);
            for (int i = 0; i < count; i++)
            {
                Guid id = GuidAt(r);
                long offset = r.ReadInt64();
                int length = r.ReadInt32();
                r.ReadUInt32();
                long nextEntry = r.BaseStream.Position;
                if (length < 24 || offset < 0 || offset > bytes.LongLength - length)
                    throw new InvalidDataException("Invalid JT segment range");
                r.BaseStream.Position = offset;
                if (GuidAt(r) != id) throw new InvalidDataException("JT segment ID mismatch");
                int type = r.ReadInt32();
                if (r.ReadInt32() != length) throw new InvalidDataException("Segment length mismatch");
                var segment = new Segment { Id = id, Type = type, Ordinal = i };
                // B-rep, metadata and other representations need no payload copy.
                if (type == 1)
                {
                    using (var compressed = R(Bytes(r, length - 24)))
                    {
                        if (compressed.ReadInt32() != 3) throw new NotSupportedException("LSG codec unsupported");
                        int compressedLength = compressed.ReadInt32();
                        if (compressedLength < 1 || compressed.ReadByte() != 3)
                            throw new NotSupportedException("LSG codec unsupported");
                        using (var xz = CODEC.DecompressLZMA(new MemoryStream(Bytes(compressed, compressedLength - 1))))
                        {
                            if (xz.Length > 512 * 1024 * 1024) throw new InvalidDataException("LSG size limit");
                            using (var reader = new BinaryReader(xz)) segment.Data = Bytes(reader, (int)xz.Length);
                        }
                    }
                }
                else if (type == 7)
                {
                    int elementLength = r.ReadInt32();
                    if (elementLength < 21 || elementLength > length - 28)
                        throw new InvalidDataException("Invalid JT shape element length");
                    Guid elementType = GuidAt(r);
                    r.ReadByte();
                    r.ReadInt32();
                    if (Type(elementType) == "10dd10ab") segment.Data = Bytes(r, elementLength - 21);
                }
                segments.Add(id, segment);
                r.BaseStream.Position = nextEntry;
            }
        }
    }

    static void Run(string input, string output)
    {
        byte[] bytes = File.ReadAllBytes(input), hash;
        using (var h = SHA256.Create()) hash = h.ComputeHash(bytes);
        using (var reader = R(bytes)) ValidateHeader(reader, input);
        if (sourceMajor <= 9) ParseLegacyFile(input);
        else ParseModernFile(bytes);
        var lsg = segments.Values.Single(s => s.Type == 1); if(sourceMajor>9)ParseLSG(lsg.Data); var root = sourceMajor==9 ? Root9() : nodes.Values.Single(n => n.Type == "10dd103e"); Walk(root.Id, Identity(), new[] { double.NaN, double.NaN, double.NaN, 1.0 }, 0, new HashSet<int>());
        var selected = new HashSet<Guid>(bindings.Select(b => b.Segment.Id));
        var expected = new HashSet<Guid>(segments.Values.Where(TriangleSegment).Select(s => s.Id));
        if (!selected.IsSubsetOf(expected) || expected.Any(id => !selected.Contains(id) && !alternateTriangleSegments.Contains(id)))
            throw new InvalidDataException("Unreferenced/unsupported triangle LOD");
        Console.WriteLine("LOD_ALTERNATE_SEGMENTS " + alternateTriangleSegments.Count(id => !selected.Contains(id)));
        Console.WriteLine("MATERIAL_ALPHA " + string.Join("; ", bindings.GroupBy(b => (byte)Math.Round(b.RGBA[3] * 255)).OrderBy(g => g.Key).Select(g => "alpha=" + g.Key + " shapes=" + g.Count()))); var file = Blank<JTFile>(); file.majorVersion = sourceMajor; file.minorVersion = sourceMinor; int surface = 0; foreach (var b in bindings.OrderBy(b => b.Segment.Ordinal)) { if(sourceMajor==8)DecodeLegacy(b, ++surface); else if(sourceMajor==9)Decode9(b, ++surface); else Decode(b, ++surface, file); } if (vertices.Count == 0 || faces.Count == 0) throw new InvalidDataException("Empty JT mesh"); using (var h = SHA256.Create()) using (var s = File.OpenRead(input)) if (!hash.SequenceEqual(h.ComputeHash(s))) throw new InvalidDataException("JT changed during conversion");
        Console.WriteLine("MIRRORED_SHAPES " + bindings.Count(binding => binding.Mirrored));
        string tmp = output + ".tmp"; try { using (var w = new BinaryWriter(File.Create(tmp))) { w.Write(Encoding.ASCII.GetBytes("JTMESH02")); w.Write(2); w.Write(hash); w.Write(vertices.Count); w.Write(faces.Count); foreach (var p in vertices) foreach (double v in p) w.Write(F(v)); foreach (var f in faces) { foreach (int i in f.Ids) w.Write(i); w.Write(f.RGB); w.Write(f.Alpha); w.Write(f.Surface); foreach (double v in f.Normal) w.Write(F(v)); } } File.Move(tmp, output); } finally { if (File.Exists(tmp)) File.Delete(tmp); }
        Console.WriteLine("PASS defaultMaterialShapes=" + defaultMaterialShapes + " vertices=" + vertices.Count + " faces=" + faces.Count + " shapes=" + surface + " colors=" + faces.Select(f => BitConverter.ToString(f.RGB)).Distinct().Count());
    }
    static int Main(string[] args) { Console.OutputEncoding = new UTF8Encoding(false); try { if (args.Length != 2) throw new ArgumentException("Usage: TxTools.JtDecoder input.jt output.jtmesh"); Run(Path.GetFullPath(args[0]), Path.GetFullPath(args[1])); return 0; } catch (NotSupportedException e) { Console.WriteLine("COMPAT_REQUIRED "+e.Message); return 2; } catch (Exception e) { Console.Error.WriteLine(e); return 1; } }
}
