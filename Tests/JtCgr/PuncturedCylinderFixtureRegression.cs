using System;using System.IO;using System.Linq;using System.Reflection;using System.Collections.Generic;using TxTools.ExportByColor;
// Real source regression: a transverse bore must not be covered by a complete barrel.
class PuncturedCylinderFixtureRegression
{
 static void Check(bool b,string s){if(!b)throw new Exception(s);}
 static double BoreClearance(float[][] triangle,double[] center)
 {
  var p=triangle.Select(v=>new[]{(double)v[0]-center[0],v[1]-center[1]}).ToArray();
  var cross=Enumerable.Range(0,3).Select(i=>p[i][0]*p[(i+1)%3][1]-p[i][1]*p[(i+1)%3][0]).ToArray();
  double area=(p[1][0]-p[0][0])*(p[2][1]-p[0][1])-(p[1][1]-p[0][1])*(p[2][0]-p[0][0]);
  if(Math.Abs(area)>1e-12&&(cross.All(x=>x>=0)||cross.All(x=>x<=0)))return 0;
  double best=double.PositiveInfinity;
  for(int i=0;i<3;i++){var a=p[i];var b=p[(i+1)%3];double dx=b[0]-a[0],dy=b[1]-a[1],length=dx*dx+dy*dy;double t=length==0?0:Math.Max(0,Math.Min(1,-(a[0]*dx+a[1]*dy)/length));best=Math.Min(best,Math.Sqrt(Math.Pow(a[0]+t*dx,2)+Math.Pow(a[1]+t*dy,2)));}
  return best;
 }
 static int Main(string[] a){AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{var p=Path.Combine(a[2],new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};try{Run(a);return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
 static void Run(string[] a)
 {
  var mesh=JtDirectBridge.Read(a[1],a[0]);var shapes=new HashSet<int>{461,477,1720,1724,1843};
  var faces=mesh.Faces.Where(f=>shapes.Contains(f.Surface)).Select(f=>new CgrWriter.Face{Idx=f.Indices,R=f.R,G=f.G,B=f.B,Opacity=f.Alpha,Surface=f.Surface,Nx=f.Normal[0],Ny=f.Normal[1],Nz=f.Normal[2]}).ToList();
  var source=faces.Select(f=>(int[])f.Idx.Clone()).ToList();var snapshots=faces.SelectMany(f=>f.Idx).Distinct().ToDictionary(v=>v,v=>(float[])mesh.Vertices[v].Clone());
  var holes=RoundHoleReconstruction.Find(mesh.Vertices,faces,new RoundHoleReconstruction.Options{IncludeExternalCylinders=true,IncludeTransitionRims=true,IncludeOpenCylinderEnds=true,IncludeTrimmedCylinderEnds=true});
  var result=RoundHoleCgrMesh.Replace(mesh.Vertices,faces,holes,Console.WriteLine);
  Check(result.Cylinders.Values.Count(c=>c.SourceSurface==461)==4,"Upper/lower transverse 8 mm bores or barrel dimensions missing");
  Check(result.Cylinders.Values.Count(c=>c.SourceSurface==477)==4,"Second barrel transverse bores missing");
  Check(result.Cylinders.Values.Count(c=>c.SourceSurface==1720)==14,"Nested barrel transverse 6 mm bores missing");
  Check(result.Cylinders.Values.Count(c=>c.SourceSurface==1724)==18,"Nearly planar stepped cap omitted its 1.55 mm bore");
  Check(result.Cylinders.Values.Count(c=>c.SourceSurface==1843)==3,"Cross-drilled shaft omitted its 2.5 mm bore");
  Check(result.Cylinders.Values.Count(c=>c.PreservedWallTessellation||c.RefinedSourceWall)==9,"Punctured source cylinders were turned into complete tubes");
  Check(result.Cylinders.Values.Count(c=>c.RefinedSourceWall)==9,"Transverse barrels, nested sleeves or cross-drilled shaft did not receive constrained display refinement");
  foreach(var c in result.Cylinders.Values.Where(c=>c.RefinedSourceWall))foreach(var f in result.Faces.Where(f=>f.Surface==c.Surface))
  {
   Func<double[],double> radius=p=>{var d=p.Select((v,k)=>v-c.Center[k]).ToArray();double axial=d.Select((v,k)=>v*c.Axis[k]).Sum();return Math.Sqrt(d.Select((v,k)=>Math.Pow(v-axial*c.Axis[k],2)).Sum());};
   foreach(int id in f.Idx)Check(Math.Abs(radius(result.Vertices[id].Select(v=>(double)v).ToArray())-c.Radius)<=.001,"Refined cylinder vertex left its analytical radius");
   for(int k=0;k<3;k++){var a1=result.Vertices[f.Idx[k]];var b1=result.Vertices[f.Idx[(k+1)%3]];Check(Math.Abs(radius(Enumerable.Range(0,3).Select(j=>((double)a1[j]+b1[j])/2).ToArray())-c.Radius)<=.0021,"Refined cylinder display sag exceeds tolerance");}
  }
  Func<IEnumerable<CgrWriter.Face>,List<float[]>,Dictionary<string,int>> edgeCounts=(fs,vs)=>{var counts=new Dictionary<string,int>();Func<int,string> point=id=>string.Join(",",vs[id].Select(v=>v.ToString("R",System.Globalization.CultureInfo.InvariantCulture)));foreach(var f in fs)for(int k=0;k<3;k++){string x=point(f.Idx[k]),y=point(f.Idx[(k+1)%3]),key=string.CompareOrdinal(x,y)<0?x+"|"+y:y+"|"+x;int n;counts.TryGetValue(key,out n);counts[key]=n+1;}return counts;};
  foreach(int shape in new[]{461,477,1720,1843})
  {
   var originalEdges=edgeCounts(faces.Where(f=>f.Surface==shape),mesh.Vertices);
   if(originalEdges.Values.All(n=>n==2)){var groups=new HashSet<int>(result.Cylinders.Values.Where(c=>c.SourceSurface==shape).Select(c=>c.Surface)){shape};Check(edgeCounts(result.Faces.Where(f=>groups.Contains(f.Surface)),result.Vertices).Values.All(n=>n==2),"Refinement introduced an open or nonmanifold source Shape "+shape);}
  }
  foreach(var c in result.Cylinders.Values.Where(c=>c.SourceSurface==461||c.SourceSurface==477))
  {
   var wall=result.Faces.Where(f=>f.Surface==c.Surface).ToList();
   if(c.PreservedWallTessellation||c.RefinedSourceWall)foreach(var f in wall)Check(BoreClearance(f.Idx.Select(id=>result.Vertices[id]).ToArray(),c.Center)>=8-.0025,"Barrel triangle interior covers the transverse opening beyond display sag and float rounding");
   foreach(int id in wall.SelectMany(f=>f.Idx).Distinct())
   {
    var v=result.Vertices[id];double distance=Math.Sqrt(Math.Pow(v[0]-c.Center[0],2)+Math.Pow(v[1]-c.Center[1],2));
    if(c.PreservedWallTessellation||c.RefinedSourceWall)Check(distance>=8-.001,"Barrel triangles cover the transverse bore opening");
    else Check(Math.Abs(distance-8)<.001,"Transverse bore radius changed");
   }
  }
  foreach(var p in snapshots)Check(mesh.Vertices[p.Key].SequenceEqual(p.Value),"Input vertex mutated");for(int i=0;i<faces.Count;i++)Check(faces[i].Idx.SequenceEqual(source[i]),"Input face mutated");
  foreach(var c in result.Cylinders.Values){var h=holes.Single(h=>h.Center==c.Center);var color=faces[h.WallTriangles[0]];Check(result.Faces.Where(f=>f.Surface==c.Surface).All(f=>f.R==color.R&&f.G==color.G&&f.B==color.B&&f.Opacity==color.Opacity),"Source cylinder material changed");}
  Console.WriteLine("PASS 9156 punctured barrels and stepped cap: 43 cylinders; source walls refined="+result.Cylinders.Values.Count(c=>c.RefinedSourceWall)+" retained="+result.Cylinders.Values.Count(c=>c.PreservedWallTessellation)+"; transverse openings, radius, display sag, closed topology, RGBA and immutable input; no CGR readback");
 }
}
