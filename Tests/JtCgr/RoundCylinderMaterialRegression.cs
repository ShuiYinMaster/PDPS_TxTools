using System;using System.Linq;using System.Collections.Generic;using TxTools.ExportByColor;
class RoundCylinderMaterialRegression
{
 static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
 static int Main(){try{Run();return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
 static void Run()
 {
  const int n=16;var vertices=new List<float[]>();var faces=new List<CgrWriter.Face>();
  for(int ring=0;ring<4;ring++)for(int i=0;i<n;i++){double a=2*Math.PI*i/n,r=ring<2?20:10;vertices.Add(new[]{(float)(r*Math.Cos(a)),(float)(r*Math.Sin(a)),ring%2==0?0f:15f});}
  Action<int,int,int,byte,byte> add=(a,b,c,r,alpha)=>faces.Add(new CgrWriter.Face{Idx=new[]{a,b,c},Surface=1,R=r,G=20,B=30,Opacity=alpha});
  for(int i=0;i<n;i++){int j=(i+1)%n;byte r=(byte)(i<8?10:200),alpha=(byte)(i<8?127:64);add(i,j,n+j,r,alpha);add(i,n+j,n+i,r,alpha);add(2*n+i,3*n+j,2*n+j,10,127);add(2*n+i,3*n+i,3*n+j,10,127);add(i,2*n+j,j,10,127);add(i,2*n+i,2*n+j,10,127);add(n+i,n+j,3*n+j,10,127);add(n+i,3*n+j,3*n+i,10,127);}
  var snapshots=vertices.Select(v=>(float[])v.Clone()).ToList();var indices=faces.Select(f=>(int[])f.Idx.Clone()).ToList();
  var holes=RoundHoleReconstruction.Find(vertices,faces,new RoundHoleReconstruction.Options{IncludeExternalCylinders=true});var result=RoundHoleCgrMesh.Replace(vertices,faces,holes,Console.WriteLine);
  Check(result.Cylinders.Count==2,"Mixed-material outer cylinder was omitted");var outer=result.Cylinders.Values.Single(c=>c.ExternalCylinder);Check(outer.RefinedSourceWall,"Mixed-material outer wall was not refined");
  var wall=result.Faces.Where(f=>f.Surface==outer.Surface).ToList();Check(wall.Any(f=>f.R==10&&f.Opacity==127)&&wall.Any(f=>f.R==200&&f.Opacity==64),"Per-face color/alpha lost");Check(result.Faces.All(f=>f.G==20&&f.B==30&&(f.R==10&&f.Opacity==127||f.R==200&&f.Opacity==64)),"Unexpected interpolated material");
  Func<float[],string> point=p=>string.Join(",",p.Select(v=>v.ToString("R",System.Globalization.CultureInfo.InvariantCulture)));var edges=new Dictionary<string,int>();foreach(var f in result.Faces)for(int k=0;k<3;k++){string a=point(result.Vertices[f.Idx[k]]),b=point(result.Vertices[f.Idx[(k+1)%3]]),key=string.CompareOrdinal(a,b)<0?a+"|"+b:b+"|"+a;int count;edges.TryGetValue(key,out count);edges[key]=count+1;}Check(edges.Values.All(count=>count==2),"Mixed-material refinement left cracks or duplicate edges");
  Check(vertices.Select((v,i)=>v.SequenceEqual(snapshots[i])).All(b=>b)&&faces.Select((f,i)=>f.Idx.SequenceEqual(indices[i])).All(b=>b),"Source mesh mutated");
  Check(result.Faces.Count==faces.Count-result.RemovedWallTriangles-result.ReplacedCapTriangles+result.AddedWallTriangles+result.AddedCapTriangles+result.RefinementAddedTriangles,"Triangle accounting changed");
  Console.WriteLine("PASS mixed RGBA cylinder: inner/outer native groups, source material boundaries, closed topology and immutable input; no CGR readback");
 }
}
