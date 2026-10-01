using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using TxTools.ExportByColor;

// Detached data only: this runner never loads PS or CATIA, or changes a source file.
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if(args.Length==1&&args[0]=="--self-test"){CompressionSelfTests.Run();return 0;}
            if(args.Length==3&&args[0]=="--package")
            {
                using(var package=new ThreeDXmlWriter(args[1],"R39FeatureBridge"))
                {
                    var part=package.PreparePart("R38线面几何",new List<string>{"R39回归","3DXML桥接"});
                    package.AddEncodedPart(part,args[2]);
                    package.Complete();
                }
                Console.WriteLine("3DXML="+args[1]);return 0;
            }
            if(args.Length<3)throw new ArgumentException("model.json output.cgr compact|features|features-planar [maxFaces] [cfv3]");
            if(args[2]!="compact"&&args[2]!="features"&&args[2]!="features-planar")throw new ArgumentException("Unknown encoder mode");
            var vertices=new List<float[]>();var faces=new List<CgrWriter.Face>();
            using(var document=JsonDocument.Parse(File.ReadAllText(args[0])))
            {
                var root=document.RootElement;JsonElement colors,surfaces;
                bool hasColors=root.TryGetProperty("colors",out colors),hasSurfaces=root.TryGetProperty("surfaces",out surfaces);
                // JsonElement's array indexer scans to the requested element. Repeated
                // colors[ordinal] on PS captures makes input loading quadratic.
                var colorRows=hasColors?colors.EnumerateArray():default(JsonElement.ArrayEnumerator);
                var surfaceRows=hasSurfaces?surfaces.EnumerateArray():default(JsonElement.ArrayEnumerator);
                foreach(var v in root.GetProperty("vertices").EnumerateArray())
                    vertices.Add(new[]{v[0].GetSingle(),v[1].GetSingle(),v[2].GetSingle()});
                foreach(var f in root.GetProperty("faces").EnumerateArray())
                {
                    if(hasColors&&!colorRows.MoveNext())throw new ArgumentException("Not enough face colors");
                    if(hasSurfaces&&!surfaceRows.MoveNext())throw new ArgumentException("Not enough source groups");
                    var color=hasColors?colorRows.Current:default(JsonElement);
                    faces.Add(new CgrWriter.Face{Idx=new[]{f[0].GetInt32(),f[1].GetInt32(),f[2].GetInt32()},
                        R=hasColors?color[0].GetByte():(byte)160,
                        G=hasColors?color[1].GetByte():(byte)160,
                        B=hasColors?color[2].GetByte():(byte)160,
                        Surface=hasSurfaces?surfaceRows.Current.GetInt32():0});
                }
                if(hasColors&&colorRows.MoveNext()||hasSurfaces&&surfaceRows.MoveNext())throw new ArgumentException("Extra face colors or source groups");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1])));
            int limit=args.Length>3?int.Parse(args[3]):20000;
            var watch=Stopwatch.StartNew();
            // Reflection allows the same runner to encode an archived pre-change source tree.
            var mode=typeof(CgrWriter).Assembly.GetType("TxTools.ExportByColor.CgrBackend");
            if(mode==null)
            {
                if(args[2]!="compact")throw new ArgumentException("Baseline only supports compact mode");
                Environment.SetEnvironmentVariable("TXTOOLS_CGR_BACKEND",null);
                CgrWriter.BuildFile(vertices,faces,args[1],limit,Console.WriteLine);
            }
            else
            {
                var method=typeof(CgrWriter).GetMethod("BuildFile",new[]{typeof(List<float[]>),typeof(List<CgrWriter.Face>),typeof(string),mode,typeof(int),typeof(Action<string>)});
                object selected=Enum.Parse(mode,args[2]=="features"?"LineFace":args[2]=="features-planar"?"LineFacePlanar":"Compact");
                method.Invoke(null,new object[]{vertices,faces,args[1],selected,limit,new Action<string>(Console.WriteLine)});
            }
            if(args.Length>4&&args[4]=="cfv3")
            {
                var stats=Cfv3Encoder.ConvertFile(args[1],Path.ChangeExtension(args[1],".3DRep"),"line-face-regression");
                Console.WriteLine("CFV3 leaves="+stats.Leaves+" bytes="+stats.OutputBytes);
            }
            Console.WriteLine("ElapsedMs="+watch.ElapsedMilliseconds);
            return 0;
        }
        catch(Exception error)
        {
            if(error is TargetInvocationException&&error.InnerException!=null)error=error.InnerException;
            Console.Error.WriteLine(error);return 1;
        }
    }
}
