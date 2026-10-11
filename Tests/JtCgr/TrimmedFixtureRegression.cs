using System;using System.IO;using System.Linq;using System.Collections.Generic;using System.Reflection;using TxTools.ExportByColor;
class TrimmedFixtureRegression
{
 static void Check(bool x,string message){if(!x)throw new Exception(message);}
 static int Main(string[] a){AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{string p=Path.Combine(a[2],new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};try{Run(a);return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
 static void Run(string[] a)
 {
  var mesh=JtDirectBridge.Read(a[1],a[0]);var shapes=new HashSet<int>{255,262,302,462};
  var faces=mesh.Faces.Where(f=>shapes.Contains(f.Surface)).Select(f=>new CgrWriter.Face{Idx=f.Indices,Surface=f.Surface,R=f.R,G=f.G,B=f.B,Opacity=f.Alpha,Nx=f.Normal[0],Ny=f.Normal[1],Nz=f.Normal[2]}).ToList();
  var snapshots=faces.SelectMany(f=>f.Idx).Distinct().ToDictionary(i=>i,i=>(float[])mesh.Vertices[i].Clone());var indices=faces.Select(f=>(int[])f.Idx.Clone()).ToList();
  var holes=RoundHoleReconstruction.Find(mesh.Vertices,faces,new RoundHoleReconstruction.Options{IncludeExternalCylinders=true,IncludeTransitionRims=true,IncludeOpenCylinderEnds=true,IncludeTrimmedCylinderEnds=true});
  Check(holes.Count(h=>h.Surface==302&&h.PairedRims)==12,"Two source trimmed bores were not identified");
  var result=RoundHoleCgrMesh.Replace(mesh.Vertices,faces,holes,Console.WriteLine);
  Check(result.Cylinders.Values.Count(c=>c.SourceSurface==302)==12&&result.Cylinders.Count==19,"Complete joined detail still omits a source cylinder");
  foreach(double z in new[]{345d,375d})
  {
   var c=result.Cylinders.Values.SingleOrDefault(v=>v.SourceSurface==302&&Math.Abs(v.Center[1]+689.5)<.001&&Math.Abs(v.Center[2]-z)<.001&&Math.Abs(v.Radius-7)<.001);
   Check(c!=null&&(c.TrimmedRimA||c.TrimmedRimB)&&Math.Abs(c.Axis[0])>.999999,"Known 7 mm bore was omitted or flattened");
   var wall=result.Faces.Where(f=>f.Surface==c.Surface).ToList();Check(wall.All(f=>f.R==0&&f.G==255&&f.B==255&&f.Opacity==255),"Cyan source wall material changed");
   var points=wall.SelectMany(f=>f.Idx).Distinct().Select(i=>result.Vertices[i]).Where(p=>p[0]<635).ToList();
   Check(points.Max(p=>p[0])-points.Min(p=>p[0])>.08,"Nonplanar end was flattened");
   Check(points.All(p=>Math.Abs(Math.Sqrt(Math.Pow(p[1]-c.Center[1],2)+Math.Pow(p[2]-c.Center[2],2))-7)<.001),"Round wall radial profile changed");
  }
  foreach(var p in snapshots)Check(mesh.Vertices[p.Key].SequenceEqual(p.Value),"Source vertex changed");for(int i=0;i<faces.Count;i++)Check(faces[i].Idx.SequenceEqual(indices[i]),"Source face changed");
  foreach(int shape in shapes)Check(result.Faces.Where(f=>f.Surface==shape).All(f=>faces.Any(s=>s.Surface==shape&&s.R==f.R&&s.G==f.G&&s.B==f.B&&s.Opacity==f.Opacity)),"A cap material was introduced or changed");
  Console.WriteLine("PASS 9156 source Shape 302: all 12 cylinders, both nonplanar 7 mm bores, joined detail 19 cylinders, source trim height, circular radius, original material and immutable input; no CGR readback");
 }
}
