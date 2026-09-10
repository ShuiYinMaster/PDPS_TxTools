using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TxTools.ExportByColor
{
    /// <summary>Replay bundle: ordinary binary STL per device/color, with a versioned JSON manifest.</summary>
    public static class CgrTestData
    {
        private static string Json(string text)
        {
            var b=new StringBuilder("\"");
            foreach(char c in text??"")
            {
                if(c=='"'||c=='\\') { b.Append('\\');b.Append(c); }
                else if(c<32) b.Append("\\u"+((int)c).ToString("x4"));
                else b.Append(c);
            }
            return b.Append('"').ToString();
        }
        private static string Hash(string file)
        {
            using(var h=SHA256.Create()) using(var s=File.OpenRead(file))
                return BitConverter.ToString(h.ComputeHash(s)).Replace("-","").ToLowerInvariant();
        }
        public static string Write(List<DeviceData> devices,string directory,string originName,Action<string,List<float[]>> writeStl)
        {
            Directory.CreateDirectory(directory);
            string manifest=Path.Combine(directory,"manifest.json");
            if(File.Exists(manifest)||Directory.GetFiles(directory).Length!=0) throw new IOException("Test bundle directory must be empty");
            var entries=new List<string>();long total=0;
            for(int di=0;di<devices.Count;di++)
            {
                var dd=devices[di];
                for(int ci=0;ci<dd.Colors.Count;ci++)
                {
                    var cg=dd.Colors[ci];if(cg.Tris.Count==0) continue;
                    foreach(var t in cg.Tris)
                    {
                        if(t==null||t.Length!=9) throw new ArgumentException("Expected 9 floats per triangle");
                        foreach(float x in t) if(float.IsNaN(x)||float.IsInfinity(x)) throw new ArgumentException("Non-finite STL coordinate");
                    }
                    string name="device_"+di.ToString("D5")+"_color_"+ci.ToString("D4")+".stl";
                    string file=Path.Combine(directory,name);writeStl(file,cg.Tris);
                    if(new FileInfo(file).Length!=84L+50L*cg.Tris.Count) throw new IOException("Unexpected STL byte count");
                    var path=new List<string>();foreach(string segment in dd.Path) path.Add(Json(segment));
                    entries.Add("{\"deviceIndex\":"+di+",\"deviceName\":"+Json(dd.Name)+",\"path\":["+string.Join(",",path)+"],\"colorIndex\":"+ci+
                        ",\"rgb\":["+cg.R+","+cg.G+","+cg.B+"],\"triangleCount\":"+cg.Tris.Count+",\"stl\":"+Json(name)+",\"sha256\":"+Json(Hash(file))+"}");
                    total+=cg.Tris.Count;
                }
            }
            if(total==0) throw new ArgumentException("No triangles to export");
            string json="{\n\"schema\":\"TxTools.CgrTestData\",\"version\":1,\"createdUtc\":"+Json(DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture))+
                ",\n\"coordinateSystem\":\"PS world axes, selected origin translation subtracted; no rotation or scale\",\"units\":\"PS API length units, unchanged\",\"originName\":"+Json(originName)+
                ",\n\"colorPolicy\":\"Existing ExportByColor geometry color selection; one RGB for every triangle in each STL\",\"triangleCount\":"+total+
                ",\n\"entries\":[\n"+string.Join(",\n",entries)+"\n]}\n";
            // Publish manifest last: incomplete bundles are not replayable.
            File.WriteAllText(manifest,json,new UTF8Encoding(false));return manifest;
        }
    }
}
