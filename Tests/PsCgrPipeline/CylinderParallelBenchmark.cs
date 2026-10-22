using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using TxTools.ExportByColor;

class CylinderParallelBenchmark
{
    static void Add(List<CgrWriter.Face> faces,int a,int b,int c,int surface)
    { faces.Add(new CgrWriter.Face{Idx=new[]{a,b,c},Surface=surface,R=80,G=130,B=190}); }
    static string Digest(List<RoundHoleReconstruction.Hole> holes)
    {
        var s=new StringBuilder();
        foreach(var h in holes)
        {
            s.Append(h.Surface).Append(':').Append(h.ExternalCylinder).Append(':').Append(h.PairedRims).Append(':');
            foreach(var a in new[]{h.Center,h.Axis,h.RimA,h.RimB})if(a!=null)foreach(double v in a)s.Append(v.ToString("R",CultureInfo.InvariantCulture)).Append(',');
            s.Append(h.Radius.ToString("R",CultureInfo.InvariantCulture)).Append(':').Append(h.Depth.ToString("R",CultureInfo.InvariantCulture));
            foreach(int f in h.WallTriangles)s.Append(',').Append(f);s.AppendLine();
        }
        using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(s.ToString())));
    }
    static string MeshDigest(RoundHoleCgrMesh.Result result)
    {
        using(var hash=SHA256.Create())using(var sink=new CryptoStream(Stream.Null,hash,CryptoStreamMode.Write))using(var w=new BinaryWriter(sink))
        {
            foreach(var v in result.Vertices)foreach(float f in v)w.Write(f);
            foreach(var f in result.Faces){foreach(int i in f.Idx)w.Write(i);w.Write(f.Surface);w.Write(f.R);w.Write(f.G);w.Write(f.B);w.Write(f.Nx);w.Write(f.Ny);w.Write(f.Nz);}
            foreach(var c in result.Cylinders.Values){w.Write(c.Surface);w.Write(c.SourceSurface);w.Write(c.Radius);w.Write(c.Depth);w.Write(c.ExternalCylinder);foreach(var a in new[]{c.Center,c.Axis,c.RimA,c.RimB})foreach(double v in a)w.Write(v);}
            w.Flush();sink.FlushFinalBlock();return BitConverter.ToString(hash.Hash);
        }
    }
    static int Main(string[] args)
    {
        try
        {
            int shapes=args.Length>0?int.Parse(args[0]):512,n=args.Length>1?int.Parse(args[1]):128;
            var vertices=new List<float[]>();var faces=new List<CgrWriter.Face>();
            for(int shape=0;shape<shapes;shape++)
            {
                int start=vertices.Count;
                for(int ring=0;ring<4;ring++)for(int i=0;i<n;i++)
                {
                    double a=2*Math.PI*i/n,r=ring<2?10:20;
                    vertices.Add(new[]{(float)(shape%32*60+r*Math.Cos(a)),(float)(shape/32*60+r*Math.Sin(a)),(float)(ring%2*20)});
                }
                for(int i=0;i<n;i++)
                {
                    int j=(i+1)%n,a=start+i,b=start+j,c=start+n+i,d=start+n+j,e=start+2*n+i,f=start+2*n+j,g=start+3*n+i,h=start+3*n+j,s=shape+1;
                    Add(faces,a,c,b,s);Add(faces,b,c,d,s);Add(faces,e,f,g,s);Add(faces,f,h,g,s);
                    Add(faces,a,b,e,s);Add(faces,b,f,e,s);Add(faces,c,g,d,s);Add(faces,d,g,h,s);
                }
            }
            var field=typeof(RoundHoleReconstruction.Options).GetField("MaxDegreeOfParallelism");
            string baseline=null,meshBaseline=null;var reports=new List<string>();
            foreach(int degree in args.Length>3?Array.ConvertAll(args[3].Split(','),int.Parse):field==null?new[]{1}:new[]{1,0,0})
            {
                var options=new RoundHoleReconstruction.Options{IncludeExternalCylinders=true,IncludeTransitionRims=true,IncludeOpenCylinderEnds=true,IncludeTrimmedCylinderEnds=true};
                if(field!=null)field.SetValue(options,degree);
                GC.Collect();GC.WaitForPendingFinalizers();
                var cpu=Process.GetCurrentProcess().TotalProcessorTime;var watch=Stopwatch.StartNew();
                var holes=RoundHoleReconstruction.Find(vertices,faces,options);watch.Stop();
                string digest=Digest(holes);
                if(holes.Count!=shapes*2)throw new Exception("Wrong cylinder count: "+holes.Count);
                if(baseline!=null&&digest!=baseline)throw new Exception("Serial/parallel geometry or order differs");baseline=digest;
                string report="version="+CgrWriter.Version+" logicalProcessors="+Environment.ProcessorCount+" degree="+degree+" shapes="+shapes+" triangles="+faces.Count+" cylinders="+holes.Count+" seconds="+watch.Elapsed.TotalSeconds.ToString("F3",CultureInfo.InvariantCulture)+" cpuSeconds="+(Process.GetCurrentProcess().TotalProcessorTime-cpu).TotalSeconds.ToString("F3",CultureInfo.InvariantCulture)+" hash="+digest;
                var display=new RoundHoleCgrMesh.DisplayOptions();var displayField=typeof(RoundHoleCgrMesh.DisplayOptions).GetField("MaxDegreeOfParallelism");
                if(displayField!=null)displayField.SetValue(display,degree);
                watch.Restart();var rebuilt=args.Length>4?null:RoundHoleCgrMesh.Replace(vertices,faces,holes,null,display);watch.Stop();
                string mesh=rebuilt==null?"skipped":MeshDigest(rebuilt);
                if(rebuilt!=null&&rebuilt.Cylinders.Count!=shapes*2)throw new Exception("Reconstruction lost cylinders");
                if(meshBaseline!=null&&mesh!=meshBaseline)throw new Exception("Serial/parallel replacement differs");meshBaseline=mesh;
                report+=" replaceSeconds="+watch.Elapsed.TotalSeconds.ToString("F3",CultureInfo.InvariantCulture)+" meshHash="+mesh;
                reports.Add(report);Console.WriteLine(report);
            }
            if(args.Length>2)File.WriteAllLines(args[2],reports);
            return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}