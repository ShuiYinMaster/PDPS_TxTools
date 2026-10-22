using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
#if NETFRAMEWORK
using Newtonsoft.Json;
#else
using System.Text.Json;
#endif
using TxTools.ExportByColor;

class PsPipelineRegression
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static ColorGroup Group(int surface, byte r, byte g, byte b)
    { return new ColorGroup { Surface = surface, R = r, G = g, B = b }; }
    static List<ColorGroup> Tube(int surface, bool ellipse = false, bool mixedWinding = false)
    {
        const int n = 32;
        var points = new List<float[]>();
        for (int ring = 0; ring < 4; ring++) for (int i = 0; i < n; i++)
        {
            double a = i * 2 * Math.PI / n, radius = ring < 2 ? 10 : 20;
            points.Add(new[] { (float)(radius * Math.Cos(a)), (float)(radius * Math.Sin(a) * (ellipse ? 1.2 : 1)), (float)(ring % 2 * 20) });
        }
        var inside = Group(surface, 17, 93, 201);
        var outside = Group(surface, 230, 120, 30);
        var caps = Group(surface, 160, 160, 160);
        Action<ColorGroup, int, int, int> add = (group, a, b, c) =>
        {
            if (mixedWinding && group.Tris.Count % 5 == 0) { int swap = b; b = c; c = swap; }
            group.Tris.Add(points[a].Concat(points[b]).Concat(points[c]).ToArray());
        };
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            add(inside, i, n + i, j); add(inside, j, n + i, n + j);
            add(outside, 2 * n + i, 2 * n + j, 3 * n + i); add(outside, 2 * n + j, 3 * n + j, 3 * n + i);
            add(caps, i, j, 2 * n + i); add(caps, j, 2 * n + j, 2 * n + i);
            add(caps, n + i, 3 * n + i, n + j); add(caps, n + j, 3 * n + i, 3 * n + j);
        }
        return new List<ColorGroup> { inside, outside, caps };
    }
    static List<CgrWriter.Face> Decode(string path, out List<float[]> vertices)
    {
        byte[] data = File.ReadAllBytes(path); vertices = new List<float[]>();
        var faces = new List<CgrWriter.Face>(); int leaf = 0;
        for (int p = 0; p + 46 <= data.Length;)
        {
            if (data[p] != 0x95 || data[p + 1] != 255 || data[p + 41] != 255 || data[p + 42] != 255 || data[p + 43] != 2 || data[p + 44] != 0 || data[p + 45] != 1) { p++; continue; }
            int end = checked(p + (int)BitConverter.ToUInt32(data, p + 2) + 1);
            typeof(FeatureTestDecoder).GetMethod("ReadLeaf", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null,
                new object[] { data, p + 41, end, leaf++, vertices, faces });
            p = end;
        }
        Check(leaf > 0, "No decodable feature leaves"); return faces;
    }
    static string TriangleKey(float[] t) { return string.Join(";", Enumerable.Range(0, 3).Select(k => string.Join(",", t.Skip(3 * k).Take(3).Select(v => BitConverter.ToInt32(BitConverter.GetBytes(v), 0)))).OrderBy(s => s, StringComparer.Ordinal)); }
    static int CountAttributes(string path, byte marker, byte length)
    {
        byte[] data = File.ReadAllBytes(path); int count = 0;
        for (int i = 0; i + 4 < data.Length; i++) if (data[i] == length && data[i + 1] == 0 && data[i + 2] == 0x33 && data[i + 3] == marker) count++;
        return count;
    }
    static int Main(string[] args)
    {
        try { Run(Path.GetFullPath(args[0])); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    static void Run(string output)
    {
        var edgeType=typeof(CgrWriter).Assembly.GetType("TxTools.ExportByColor.CylinderEdgeComparer",true);
        var comparer=(IEqualityComparer<long>)edgeType.GetField("Instance",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
        var hashes=new HashSet<int>();var oldHashes=new HashSet<int>();
        for(int i=0;i<100000;i++){long key=((long)(2*i)<<32)|(uint)(2*i+1);hashes.Add(comparer.GetHashCode(key));oldHashes.Add(key.GetHashCode());Check(comparer.Equals(key,key)&&!comparer.Equals(key,key+1),"Edge equality changed");}
        Check(hashes.Count>99000&&oldHashes.Count==1,"Packed edge hash distribution regressed");
        Console.WriteLine("PASS edge hash: distinct new="+hashes.Count+" old="+oldHashes.Count);
        Check(!Directory.Exists(output), "Use a new output directory"); Directory.CreateDirectory(output);
        var report = new List<object>();
        foreach (string name in new[] { "tube", "mixed_winding", "ellipse_fallback", "separate_geometries", "mixed_sheet" })
        {
            var groups = Tube(7, name == "ellipse_fallback", name == "mixed_winding");
            if (name == "separate_geometries") groups.AddRange(Tube(8));
            if (name == "mixed_sheet")
            {
                var sheet = Group(9, 11, 222, 77);
                sheet.Tris.Add(new float[] { 80, 0, 0, 90, 0, 0, 90, 10, 0 });
                sheet.Tris.Add(new float[] { 80, 0, 0, 90, 10, 0, 80, 10, 0 }); groups.Add(sheet);
            }
            var snapshots = groups.SelectMany(g => g.Tris).Select(t => (float[])t.Clone()).ToList();
            string path = Path.Combine(output, name + ".cgr");
            var result = PsCgrPipeline.BuildFile(groups, path, CgrBackend.LineFace, true, Console.WriteLine);
            int expected = name == "ellipse_fallback" ? 0 : name == "separate_geometries" ? 4 : 2;
            Check(result.Cylinders == expected, name + ": incorrect cylinder count " + result.Cylinders);
            Check(result.InnerCylinders == expected / 2 && result.OuterCylinders == expected / 2, "Inner/outer orientation lost");
            Check(groups.SelectMany(g => g.Tris).Select((t, i) => t.SequenceEqual(snapshots[i])).All(v => v), "PS snapshot mutated");
            List<float[]> vertices; var faces = Decode(path, out vertices);
            Check(faces.Any(f => f.R == 17 && f.G == 93 && f.B == 201) && faces.Any(f => f.R == 230 && f.G == 120 && f.B == 30), "Source materials lost");
            int cylinders = CountAttributes(path, 0x33, 26), circles = CountAttributes(path, 0x37, 18);
            Check(cylinders >= expected && circles >= expected * 2, "Native cylinder/circle attributes missing");
            if (name == "ellipse_fallback")
            {
                Check(faces.Count == snapshots.Count, "Unsupported ellipse lost triangles");
                var before = snapshots.Select(TriangleKey).OrderBy(s => s).ToArray();
                var after = faces.Select(f => TriangleKey(f.Idx.SelectMany(i => vertices[i]).ToArray())).OrderBy(s => s).ToArray();
                Check(before.SequenceEqual(after), "Fallback geometry changed");
            }
            if (name == "mixed_sheet") Check(faces.Count(f => f.G == 222) == 2, "Unrelated open sheet lost or duplicated");
            report.Add(new { model = name, variant = "ps_reconstruction", path, cylinders = result.Cylinders,
                inner = result.InnerCylinders, outer = result.OuterCylinders, triangles = faces.Count, cylinder_attributes = cylinders,
                circle_attributes = circles, source_unchanged = true, capture = name == "tube" });
            Console.WriteLine("PASS PS triangle snapshot " + name + " inner=" + result.InnerCylinders + " outer=" + result.OuterCylinders + " triangles=" + faces.Count);
        }
        // The fast route must never invoke cylinder fitting or emit analytic attributes.
        var plainGroups=Tube(7);var plainSnapshot=plainGroups.SelectMany(g=>g.Tris).Select(t=>(float[])t.Clone()).ToList();
        string plainPath=Path.Combine(output,"lineface_only.cgr");var plainLogs=new List<string>();
        var plain=PsCgrPipeline.BuildFile(plainGroups,plainPath,CgrBackend.LineFace,false,plainLogs.Add);
        Check(plain.Candidates==0&&plain.Cylinders==0,"Compression-only route performed reconstruction");
        Check(!File.Exists(plainPath+".cylinders.log")&&!plainLogs.Any(s=>s.Contains("[PS cylinders]")||s.Contains("圆柱识别")),"Compression route entered cylinder fitting");
        Check(CountAttributes(plainPath,0x33,26)==0,"Compression-only file contains native cylinders");
        Check(plainGroups.SelectMany(g=>g.Tris).Select((t,i)=>t.SequenceEqual(plainSnapshot[i])).All(v=>v),"Compression route mutated snapshot");
        Console.WriteLine("PASS PS line/face compression-only route: cylinder fitting skipped");
        // Unsupported backend combinations fail before creating output.
        string invalid = Path.Combine(output, "invalid.cgr"); bool rejected = false;
        try { PsCgrPipeline.BuildFile(Tube(7), invalid, CgrBackend.Compact, true); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected && !File.Exists(invalid), "Invalid reconstruction route accepted");
#if NETFRAMEWORK
        File.WriteAllText(Path.Combine(output, "variants.json"), JsonConvert.SerializeObject(new { variants = report }, Formatting.Indented));
#else
        File.WriteAllText(Path.Combine(output, "variants.json"), JsonSerializer.Serialize(new { variants = report }, new JsonSerializerOptions { WriteIndented = true }));
#endif
    }
}
