using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using TxTools.ExportByColor;

// Source-mesh regression for the 9156 fixture. No CGR decoding/readback.
class TrimIntersectionFixtureRegression
{
    static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
    static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{string p=Path.Combine(args[2],new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
        try{Run(args);return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
    static void Run(string[] args)
    {
        var mesh=JtDirectBridge.Read(args[1],args[0]);var shapes=new HashSet<int>{643,1037,1191};
        var faces=mesh.Faces.Select(f=>new CgrWriter.Face{Idx=f.Indices,Surface=f.Surface,R=f.R,G=f.G,B=f.B,Opacity=f.Alpha,Nx=f.Normal[0],Ny=f.Normal[1],Nz=f.Normal[2]}).ToList();
        var selected=faces.Where(f=>shapes.Contains(f.Surface)).ToList();
        var snapshots=selected.SelectMany(f=>f.Idx).Distinct().ToDictionary(i=>i,i=>(float[])mesh.Vertices[i].Clone());
        var indices=selected.Select(f=>(int[])f.Idx.Clone()).ToList();
        var holes=RoundHoleReconstruction.Find(mesh.Vertices,selected,new RoundHoleReconstruction.Options{IncludeExternalCylinders=true,IncludeTransitionRims=true,IncludeOpenCylinderEnds=true,IncludeTrimmedCylinderEnds=true});
        var map=faces.Select((f,i)=>new{f.Idx,i}).ToDictionary(v=>v.Idx,v=>v.i);
        foreach(var h in holes)h.WallTriangles=h.WallTriangles.Select(i=>map[selected[i].Idx]).ToArray();
        var result=RoundHoleCgrMesh.Replace(mesh.Vertices,faces,holes,Console.WriteLine);
        Check(result.Cylinders.Count==16,"Near-tangent facet intersections still omit one of the 16 source cylinders");
        foreach(var target in new[]{new[]{643d,601.806716918945,-756.500004659376,45.0000050797175,3.32352417417636},new[]{1037d,548.969275133823,-719.500019207334,794.499893704379,2.06149966596842},new[]{1191d,628.005264282227,-721.500004691753,754.999950161931,6.99998083251293}})
        {
            var c=result.Cylinders.Values.Single(v=>v.SourceSurface==(int)target[0]&&Enumerable.Range(0,3).All(k=>Math.Abs(v.Center[k]-target[k+1])<.001)&&Math.Abs(v.Radius-target[4])<.001);
            Check(c.TrimmedRimA||c.TrimmedRimB,"Source trimmed end lost its profile");
            var h=holes.Single(v=>v.Center==c.Center);var material=faces[h.WallTriangles[0]];
            var wall=result.Faces.Where(f=>f.Surface==c.Surface).ToList();
            Check(wall.All(f=>f.R==material.R&&f.G==material.G&&f.B==material.B&&f.Opacity==material.Opacity),"Cylinder source RGBA changed");
            foreach(int i in wall.SelectMany(f=>f.Idx).Distinct())
            {
                var point=result.Vertices[i];var d=Enumerable.Range(0,3).Select(k=>point[k]-c.Center[k]).ToArray();double axial=d.Zip(c.Axis,(x,y)=>x*y).Sum();
                double radius=Math.Sqrt(Enumerable.Range(0,3).Sum(k=>Math.Pow(d[k]-axial*c.Axis[k],2)));
                Check(Math.Abs(radius-c.Radius)<.001,"A display seam correction moved the circular wall off its radius");
            }
        }
        foreach(var p in snapshots)Check(mesh.Vertices[p.Key].SequenceEqual(p.Value),"Input vertices mutated");
        for(int i=0;i<selected.Count;i++)Check(selected[i].Idx.SequenceEqual(indices[i]),"Input indices mutated");
        Check(!result.Diagnostics.Any(s=>s.Contains("trim-clip-lost-")||s.Contains("trim-clip-cap-seam")||s.Contains("trim-clip-cap-source-plane-drift")),"Facet or seam guard failed");
        Console.WriteLine("PASS 9156 near-tangent trimmed caps: 16 cylinders; three formerly omitted targets; original radius, RGBA and immutable source; no CGR readback");
    }
}
