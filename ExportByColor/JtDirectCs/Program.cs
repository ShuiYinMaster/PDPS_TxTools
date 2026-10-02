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
class Program
{
    sealed class Segment { public Guid Id; public int Ordinal, Type; public byte[] Data; }
    sealed class Node { public string Type, Text; public int Id; public int[] Attributes = new int[0], Children = new int[0]; public Guid Segment; public double[] Matrix, RGBA; public uint Inhibit, Final; public byte State; }
    sealed class Binding { public Segment Segment; public double[] Matrix, RGBA; }
    sealed class Face { public int[] Ids; public byte[] RGB; public int Surface; public double[] Normal; }
    static Dictionary<int, Node> nodes = new Dictionary<int, Node>();
    static Dictionary<Guid, Segment> segments = new Dictionary<Guid, Segment>();
    static List<Binding> bindings = new List<Binding>();
    static List<double[]> vertices = new List<double[]>(); static List<Face> faces = new List<Face>();
    static T Blank<T>() { return (T)FormatterServices.GetUninitializedObject(typeof(T)); }
    static BinaryReader R(byte[] b) { return new BinaryReader(new MemoryStream(b)); }
    static byte[] Bytes(BinaryReader r, int n) { if (n < 0 || n > r.BaseStream.Length - r.BaseStream.Position) throw new InvalidDataException("Truncated JT data"); return r.ReadBytes(n); }
    static Guid GuidAt(BinaryReader r) { return new Guid(Bytes(r, 16)); }
    static int Count(BinaryReader r) { int n = r.ReadInt32(); if (n < 0 || n > 5000000) throw new InvalidDataException("Invalid JT count"); return n; }
    static double[] Identity() { var m = new double[16]; for (int i = 0; i < 16; i += 5) m[i] = 1; return m; }
    static double[] Multiply(double[] a, double[] b) { var c = new double[16]; for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) for (int k = 0; k < 4; k++) c[i * 4 + j] += a[i * 4 + k] * b[k * 4 + j]; return c; }
    static double[] Transform(double[] p, double[] m, bool point) { return Enumerable.Range(0, 3).Select(k => p[0] * m[k] + p[1] * m[4 + k] + p[2] * m[8 + k] + (point ? m[12 + k] : 0)).ToArray(); }
    static double[] Normalize(double[] p) { double l = Math.Sqrt(p.Sum(v => v * v)); return l > 1e-12 ? p.Select(v => v / l).ToArray() : new double[3]; }
    static double[] Cross(double[] a, double[] b, double[] c) { double x = b[0] - a[0], y = b[1] - a[1], z = b[2] - a[2], u = c[0] - a[0], v = c[1] - a[1], w = c[2] - a[2]; return new[] { y * w - z * v, z * u - x * w, x * v - y * u }; }
    static void Rigid(double[] m) { if (m.Any(v => double.IsNaN(v) || double.IsInfinity(v)) || Math.Abs(m[3]) + Math.Abs(m[7]) + Math.Abs(m[11]) + Math.Abs(m[15] - 1) > 1e-8) throw new InvalidDataException("Invalid affine transform"); for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) if (Math.Abs(Enumerable.Range(0, 3).Sum(k => m[4 * i + k] * m[4 * j + k]) - (i == j ? 1 : 0)) > 1e-5) throw new InvalidDataException("Only rigid JT transforms supported"); double d = m[0] * (m[5] * m[10] - m[6] * m[9]) - m[1] * (m[4] * m[10] - m[6] * m[8]) + m[2] * (m[4] * m[9] - m[5] * m[8]); if (Math.Abs(d - 1) > 1e-5) throw new InvalidDataException("Mirrored JT transform unsupported"); }
    static string Type(Guid g) { return g.ToString().Substring(0, 8); }
    static void ParseElement(BinaryReader r)
    {
        int length = Count(r); if (length < 21) throw new InvalidDataException("Invalid element"); long end = r.BaseStream.Position + length; if (end > r.BaseStream.Length) throw new InvalidDataException("Truncated element");
        string type = Type(GuidAt(r)); byte baseType = r.ReadByte(); int id = r.ReadInt32(); var body = Bytes(r, (int)(end - r.BaseStream.Position)); var n = new Node { Id = id, Type = type }; using (var b = R(body))
        {
            if (baseType <= 2) { b.ReadByte(); b.ReadUInt32(); int count = Count(b); n.Attributes = Enumerable.Range(0, count).Select(_ => b.ReadInt32()).ToArray(); }
            if (new[] { "10dd103e", "10dd101b", "10dd102c", "10dd104c", "10dd10f3", "ce357245", "ce357244" }.Contains(type)) { b.ReadByte(); int count = Count(b); n.Children = Enumerable.Range(0, count).Select(_ => b.ReadInt32()).ToArray(); }
            else if (type == "10dd102a") { b.ReadByte(); n.Children = new[] { b.ReadInt32() }; }
            else if (type == "10dd1083") { b.BaseStream.Position = 10; b.ReadByte(); ushort mask = b.ReadUInt16(); int count = Enumerable.Range(0, 16).Count(i => (mask & (0x8000 >> i)) != 0); bool wide = body.Length - 13 > count * 6; n.Matrix = Identity(); for (int i = 0; i < 16; i++) if ((mask & (0x8000 >> i)) != 0) n.Matrix[i] = wide ? b.ReadDouble() : b.ReadSingle(); }
            else if (type == "10dd1030") { if (body.Length != 93 || body[10] != 1) throw new InvalidDataException("Unsupported material layout"); b.BaseStream.Position = 1; n.State = b.ReadByte(); n.Inhibit = b.ReadUInt32(); n.Final = b.ReadUInt32(); b.BaseStream.Position = 29; n.RGBA = Enumerable.Range(0, 4).Select(_ => (double)b.ReadSingle()).ToArray(); if (n.RGBA.Any(v => double.IsNaN(v) || v < 0 || v > 1)) throw new InvalidDataException("Invalid RGBA"); }
            else if (type == "10dd106e") { b.BaseStream.Position = 6; int count = Count(b); n.Text = Encoding.Unicode.GetString(Bytes(b, Math.Min(count * 2, (int)(b.BaseStream.Length - b.BaseStream.Position)))).TrimEnd('\0'); }
            else if (baseType == 8) { b.BaseStream.Position = 6; n.Segment = GuidAt(b); }
        }
        nodes.Add(id, n);
    }
    static void ParseLSG(byte[] data)
    {
        using (var r = R(data))
        {
            for (int seq = 0; seq < 2; seq++) { while (true) { long p = r.BaseStream.Position; int len = Count(r); Guid g = GuidAt(r); r.BaseStream.Position = p; if (g == new Guid("ffffffff-ffff-ffff-ffff-ffffffffffff")) { if (len != 16) throw new InvalidDataException("Invalid end marker"); r.BaseStream.Position = p + 20; break; } ParseElement(r); } }
            if (r.ReadUInt16() != 1) throw new InvalidDataException("Property table version"); int count = Count(r); for (int i = 0; i < count; i++) { int id = r.ReadInt32(); while (true) { int key = r.ReadInt32(); if (key == 0) break; int value = r.ReadInt32(); if (nodes[key].Text == "JT_LLPROP_SHAPEIMPL") nodes[id].Segment = nodes[value].Segment; } }
            if (r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("Trailing LSG bytes");
        }
    }
    static void Walk(int id, double[] matrix, double[] rgba, uint final, HashSet<int> path)
    {
        if (path.Count > 1000 || !path.Add(id)) throw new InvalidDataException("Cyclic JT graph"); var n = nodes[id]; rgba = (double[])rgba.Clone();
        foreach (int attr in n.Attributes) { var a = nodes[attr]; if (a.RGBA != null && (a.State & 4) == 0) { uint allowed = ~a.Inhibit & ((a.State & 2) != 0 ? uint.MaxValue : ~final); if ((allowed & 64) != 0) Array.Copy(a.RGBA, rgba, 3); if ((allowed & 128) != 0) rgba[3] = a.RGBA[3]; final |= a.Final & allowed; } if (a.Matrix != null) { if (n.Type != "10dd102a") throw new InvalidDataException("Transform on non-instance node"); matrix = Multiply(a.Matrix, matrix); } }
        if (n.Type == "10dd1077") { Segment s; if (n.Segment == Guid.Empty || !segments.TryGetValue(n.Segment, out s)) throw new InvalidDataException("Missing shape binding"); if (rgba.Take(3).Any(double.IsNaN) || rgba[3] != 1) throw new InvalidDataException("Missing/transparent shape material"); Rigid(matrix); bindings.Add(new Binding { Segment = s, Matrix = matrix, RGBA = rgba }); }
        foreach (int child in n.Children) Walk(child, matrix, rgba, final, path); path.Remove(id);
    }
    static void Decode(Binding binding, int surface, JTFile file)
    {
        var body = binding.Segment.Data; var reader = new Reader(new MemoryStream(body), file); reader.Position = 41;
        // Read topology separately so unsupported vertex layouts fail before decoding.
        var deg = new List<int[]>(); for (int i = 0; i < 8; i++) deg.Add(Int32CDP.ReadVecI32(reader, PredictorType.PredNull).ToArray());
        var val = Int32CDP.ReadVecI32(reader, PredictorType.PredNull); var groups = Int32CDP.ReadVecI32(reader, PredictorType.PredNull); var flags = Int32CDP.ReadVecI32(reader, PredictorType.PredLag1);
        var masks = new List<int[]>(); for (int i = 0; i < 8; i++) masks.Add(Int32CDP.ReadVecI32(reader, PredictorType.PredNull).ToArray()); var next = Int32CDP.ReadVecI32(reader, PredictorType.PredNull); var high = reader.ReadVecU32().Select(v => (long)v).ToArray(); var splits = Int32CDP.ReadVecI32(reader, PredictorType.PredLag1); var positions = Int32CDP.ReadVecI32(reader, PredictorType.PredNull); reader.ReadU32(); long vertexPos = reader.Position; if (reader.ReadU64() != 10) throw new InvalidDataException("Only JT bindings 0xA supported"); if (reader.ReadU8() != 0) throw new InvalidDataException("Quantized coordinates unsupported"); reader.Position = vertexPos;
        var records = new TopologicallyCompressedVertexRecords(reader); var driver = new MeshCoderDriver(); driver.setInputData(val, deg, groups, flags, masks, next, null, high, splits, positions); driver.decode(); var mesh = driver.DecodedMesh;
        var xyz = records.compressedVertexCoordinateArray.vertexCoordinates; var normals = records.compressedVertexNormalArray.normalCoordinates; int offset = vertices.Count; var local = new List<double[]>(); for (int i = 0; i < xyz.Count; i += 3) local.Add(Transform(new[] { (double)xyz[i], xyz[i + 1], xyz[i + 2] }, binding.Matrix, true)); vertices.AddRange(local); byte[] rgb = binding.RGBA.Take(3).Select(v => (byte)Math.Round(v * 255)).ToArray();
        for (int f = 0; f < mesh.numFaces(); f++) { int nv = mesh.faceNumVts(f); for (int k = 1; k < nv - 1; k++) { int[] ids = { mesh.faceVtx(f, 0), mesh.faceVtx(f, k), mesh.faceVtx(f, k + 1) }; if (ids.Any(i => i < 0 || i >= local.Count)) throw new InvalidDataException("Invalid vertex index"); var geo = Cross(local[ids[0]], local[ids[1]], local[ids[2]]); var original = ids.Select(i => new[] { (double)xyz[i * 3], xyz[i * 3 + 1], xyz[i * 3 + 2] }).ToArray(); var sourceGeo = Cross(original[0], original[1], original[2]); if (Math.Sqrt(sourceGeo.Sum(v => v * v)) <= 1e-12) continue; var sum = new double[3];  foreach (int i in ids) { int ni = mesh.faceVtxAttr(f, i);  if (ni < 0) continue; if (ni * 3 + 2 >= normals.Count) throw new InvalidDataException("Invalid normal index"); var norm = Normalize(Transform(new[] { (double)normals[ni * 3], normals[ni * 3 + 1], normals[ni * 3 + 2] }, binding.Matrix, false)); for (int j = 0; j < 3; j++) sum[j] += norm[j]; }  sum = Normalize(sum); if (Enumerable.Range(0, 3).Sum(j => sum[j] * geo[j]) < 0) sum = sum.Select(v => -v).ToArray(); faces.Add(new Face { Ids = ids.Select(i => i + offset).ToArray(), RGB = rgb, Surface = surface, Normal = sum }); } }
        if (vertices.Count > 5000000 || faces.Count > 5000000) throw new InvalidDataException("Mesh size limit"); Console.WriteLine("Shape " + surface + " vertices=" + local.Count + " totalFaces=" + faces.Count);
    }
    static float F(double v) { float f = (float)v; if (float.IsNaN(f) || float.IsInfinity(f)) throw new InvalidDataException("Nonfinite mesh"); return f; }
    static void ValidateHeader(BinaryReader r,string input)
    {
        string header=Encoding.ASCII.GetString(Bytes(r,80)).TrimEnd('\0',' ','\r','\n');
        byte order=r.ReadByte();
        Console.WriteLine("DECODER=TxTools.JtDecoder version-routing-v2");
        Console.WriteLine("INPUT="+input);
        Console.WriteLine("JT_HEADER="+header+" BYTE_ORDER="+order);
        var version=System.Text.RegularExpressions.Regex.Match(header,@"^Version\s+(\d+)\.(\d+)\s+JT(?:\s|$)");
        if(!version.Success)throw new InvalidDataException("Invalid JT header: "+header);
        int major=int.Parse(version.Groups[1].Value),minor=int.Parse(version.Groups[2].Value);
        if(order>1)throw new InvalidDataException("Invalid JT byte-order marker: "+order);
        if(order!=0||major!=10||minor!=6)
            throw new NotSupportedException("Direct decoder does not support JT "+major+"."+minor+
                " byteOrder="+order+"; use the loaded PS native representation. Source: "+input);
    }
    static void Run(string input, string output)
    {
        byte[] bytes = File.ReadAllBytes(input), hash; using (var h = SHA256.Create()) hash = h.ComputeHash(bytes); using (var r = R(bytes)) { ValidateHeader(r,input); r.ReadInt32(); long toc = r.ReadInt64(); r.BaseStream.Position = toc; int count = Count(r); for (int i = 0; i < count; i++) { Guid id = GuidAt(r); long offset = r.ReadInt64(); int len = Count(r); r.ReadUInt32(); long back = r.BaseStream.Position; r.BaseStream.Position = offset + 16; int type = r.ReadInt32(); if (r.ReadInt32() != len) throw new InvalidDataException("Segment length mismatch"); byte[] data = Bytes(r, len - 24); if (type == 1) { using (var cr = R(data)) { if (cr.ReadInt32() != 3) throw new InvalidDataException("LSG codec unsupported"); int cl = Count(cr); if (cr.ReadByte() != 3) throw new InvalidDataException("LSG codec unsupported"); using (var xz = CODEC.DecompressLZMA(new MemoryStream(Bytes(cr, cl - 1)))) { if (xz.Length > 512 * 1024 * 1024) throw new InvalidDataException("LSG size limit"); using (var br = new BinaryReader(xz)) data = br.ReadBytes((int)xz.Length); } } } else if (type == 7) { using (var er = R(data)) { int el = Count(er); Guid g = GuidAt(er); er.ReadByte(); er.ReadInt32(); data = Type(g) == "10dd10ab" ? Bytes(er, el - 21) : null; } } else data = null; segments.Add(id, new Segment { Id = id, Type = type, Ordinal = i, Data = data }); r.BaseStream.Position = back; } }
        var lsg = segments.Values.Single(s => s.Type == 1); ParseLSG(lsg.Data); var root = nodes.Values.Single(n => n.Type == "10dd103e"); Walk(root.Id, Identity(), new[] { double.NaN, double.NaN, double.NaN, 1.0 }, 0, new HashSet<int>());
        var expected = new HashSet<Guid>(segments.Values.Where(s => s.Type == 7 && s.Data != null).Select(s => s.Id)); if (!expected.SetEquals(bindings.Select(b => b.Segment.Id))) throw new InvalidDataException("Unreferenced/unsupported triangle LOD"); var file = Blank<JTFile>(); file.majorVersion = 10; file.minorVersion = 6; int surface = 0; foreach (var b in bindings.OrderBy(b => b.Segment.Ordinal)) Decode(b, ++surface, file); if (vertices.Count == 0 || faces.Count == 0) throw new InvalidDataException("Empty JT mesh"); using (var h = SHA256.Create()) using (var s = File.OpenRead(input)) if (!hash.SequenceEqual(h.ComputeHash(s))) throw new InvalidDataException("JT changed during conversion");
        string tmp = output + ".tmp"; try { using (var w = new BinaryWriter(File.Create(tmp))) { w.Write(Encoding.ASCII.GetBytes("JTMESH01")); w.Write(1); w.Write(hash); w.Write(vertices.Count); w.Write(faces.Count); foreach (var p in vertices) foreach (double v in p) w.Write(F(v)); foreach (var f in faces) { foreach (int i in f.Ids) w.Write(i); w.Write(f.RGB); w.Write(f.Surface); foreach (double v in f.Normal) w.Write(F(v)); } } File.Move(tmp, output); } finally { if (File.Exists(tmp)) File.Delete(tmp); }
        Console.WriteLine("PASS vertices=" + vertices.Count + " faces=" + faces.Count + " shapes=" + surface + " colors=" + faces.Select(f => BitConverter.ToString(f.RGB)).Distinct().Count());
    }
    static int Main(string[] args) { try { if (args.Length != 2) throw new ArgumentException("Usage: TxTools.JtDecoder input.jt output.jtmesh"); Run(Path.GetFullPath(args[0]), Path.GetFullPath(args[1])); return 0; } catch (NotSupportedException e) { Console.WriteLine("COMPAT_REQUIRED "+e.Message); return 2; } catch (Exception e) { Console.Error.WriteLine(e); return 1; } }
}
