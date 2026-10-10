using System;using System.IO;using System.Linq;using System.Collections.Generic;using System.Reflection;using TxTools.ExportByColor;
class RoundCylinderTransitionRegression
{
 static void Check(bool value,string text){if(!value)throw new Exception(text);}
 static string Point(float[] p){return string.Join(",",p.Select(x=>x.ToString("R",System.Globalization.CultureInfo.InvariantCulture)));}
 static int Main(string[] a){AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{var p=Path.Combine(a[0],new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};try{foreach(bool bore in new[]{false,true})foreach(bool rotated in new[]{false,true})Run(bore,rotated);RunCapPlaneJitter();return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
 static void RunCapPlaneJitter()
 {
  const int n=16;var v=new List<float[]>();var f=new List<CgrWriter.Face>();
  for(int ring=0;ring<4;ring++)for(int i=0;i<n;i++){double a=2*Math.PI*i/n,r=ring<2?1:200,x=r*Math.Cos(a);v.Add(new[]{(float)x,(float)(r*Math.Sin(a)),(float)(ring%2*20+(ring<2?x*.0005:0))});}
  Action<int,int,int> add=(a,b,c)=>f.Add(new CgrWriter.Face{Idx=new[]{a,b,c},Surface=1,R=71,G=93,B=115,Opacity=128});
  for(int i=0;i<n;i++){int j=(i+1)%n;add(i,n+j,j);add(i,n+i,n+j);add(i,2*n+j,2*n+i);add(i,j,2*n+j);add(n+i,3*n+i,3*n+j);add(n+i,3*n+j,n+j);add(2*n+i,2*n+j,3*n+j);add(2*n+i,3*n+j,3*n+i);}
  var holes=RoundHoleReconstruction.Find(v,f);var rmesh=RoundHoleCgrMesh.Replace(v,f,holes,Console.WriteLine);Check(rmesh.Cylinders.Count==1,"Small rim noise tilted the axis enough to reject the large cap");var cylinder=rmesh.Cylinders.Values.Single();Check(Math.Abs(cylinder.Radius-1)<.001&&Math.Abs(cylinder.Axis[0])<.00001&&Math.Abs(cylinder.Depth-20)<.001,"Planar face evidence failed to stabilize the cylinder axis");
  var edges=new Dictionary<string,int>();foreach(var face in rmesh.Faces)for(int k=0;k<3;k++){string a=Point(rmesh.Vertices[face.Idx[k]]),b=Point(rmesh.Vertices[face.Idx[(k+1)%3]]);string key=string.CompareOrdinal(a,b)<0?a+"|"+b:b+"|"+a;int count;edges.TryGetValue(key,out count);edges[key]=count+1;}Check(edges.Values.All(count=>count==2),"Large cap/rim-noise replacement contains cracks");Console.WriteLine("PASS tiny rim-plane perturbation beside a large cap; stabilized axis and closed mesh");
 }
 static void Run(bool bore,bool rotated)
 {
  const int n=32;var v=new List<float[]>();var f=new List<CgrWriter.Face>();double[] radii=bore?new[]{11d,10d,10d,11d}:new[]{18d,20d,20d,18d};double[] h={0,2,13,15};
  for(int ring=0;ring<4;ring++)for(int i=0;i<n;i++){double a=2*Math.PI*i/n;v.Add(new[]{(float)(radii[ring]*Math.Cos(a)),(float)(radii[ring]*Math.Sin(a)),(float)h[ring]});}
  Action<int,int,int> add=(a,b,c)=>f.Add(new CgrWriter.Face{Idx=new[]{a,b,c},Surface=1,R=71,G=93,B=115,Opacity=128});
  for(int ring=0;ring<3;ring++)for(int i=0;i<n;i++){int j=(i+1)%n,a=ring*n+i,b=ring*n+j,c=(ring+1)*n+j,d=(ring+1)*n+i;if(bore){add(a,c,b);add(a,d,c);}else{add(a,b,c);add(a,c,d);}}
  if(bore){for(int ring=0;ring<2;ring++)for(int i=0;i<n;i++){double a=2*Math.PI*i/n;v.Add(new[]{(float)(25*Math.Cos(a)),(float)(25*Math.Sin(a)),ring*15f});}for(int i=0;i<n;i++){int j=(i+1)%n;add(i,4*n+i,4*n+j);add(i,4*n+j,j);add(3*n+i,3*n+j,5*n+j);add(3*n+i,5*n+j,5*n+i);add(4*n+i,4*n+j,5*n+j);add(4*n+i,5*n+j,5*n+i);}}
  else{int b=v.Count;v.Add(new[]{0f,0f,0f});int t=v.Count;v.Add(new[]{0f,0f,15f});for(int i=0;i<n;i++){int j=(i+1)%n;add(b,j,i);add(t,3*n+i,3*n+j);}}
  if(rotated)foreach(var p in v){double x=p[0],y=p[1],z=p[2];p[0]=(float)(1000+.8*x+.36*y+.48*z);p[1]=(float)(2000+.6*x-.48*y-.64*z);p[2]=(float)(3000+.8*y-.6*z);}
  var original=v.Select(p=>(float[])p.Clone()).ToList();var oldFaces=f.Select(p=>(int[])p.Idx.Clone()).ToList();
  var holes=RoundHoleReconstruction.Find(v,f,new RoundHoleReconstruction.Options{IncludeExternalCylinders=true,IncludeTransitionRims=true});var r=RoundHoleCgrMesh.Replace(v,f,holes,Console.WriteLine);
  Check(r.Cylinders.Count==(bore?2:1),"Chamfer-adjacent cylindrical wall missed");var target=r.Cylinders.Values.Single(c=>Math.Abs(c.Radius-(bore?10:20))<.005);Check(target.ExternalCylinder!=bore&&Math.Abs(target.Depth-11)<.005,"Transition/cylinder classification or dimensions changed");
  Check(r.Faces.All(p=>p.R==71&&p.G==93&&p.B==115&&p.Opacity==128),"RGBA changed");Check(v.Select((p,i)=>p.SequenceEqual(original[i])).All(x=>x)&&f.Select((p,i)=>p.Idx.SequenceEqual(oldFaces[i])).All(x=>x),"Source mesh mutated");
  var edges=new Dictionary<string,int>();foreach(var face in r.Faces)for(int k=0;k<3;k++){string a=Point(r.Vertices[face.Idx[k]]),b=Point(r.Vertices[face.Idx[(k+1)%3]]);string key=string.CompareOrdinal(a,b)<0?a+"|"+b:b+"|"+a;int count;edges.TryGetValue(key,out count);edges[key]=count+1;}Check(edges.Values.All(count=>count==2),"Crack or overlapping interface at cylinder/chamfer/cap");
  Console.WriteLine("PASS "+(bore?"chamfered bore":"chamfered shaft")+" "+(rotated?"arbitrary axis":"axis Z")+"; closed cap/chamfer joins, dimensions, RGBA and immutable input");
  if(!bore&&!rotated)foreach(string invalid in new[]{"ellipse","cone","open-wall"})
  {
   var bad=original.Select(p=>(float[])p.Clone()).ToList();var badFaces=new List<CgrWriter.Face>(f);
   if(invalid=="ellipse")foreach(var p in bad)p[0]*=1.2f;
   if(invalid=="cone")for(int i=2*n;i<3*n;i++){bad[i][0]*=1.1f;bad[i][1]*=1.1f;}
   if(invalid=="open-wall")badFaces.RemoveAt(2*n);
   var candidates=RoundHoleReconstruction.Find(bad,badFaces,new RoundHoleReconstruction.Options{IncludeExternalCylinders=true,IncludeTransitionRims=true});Check(candidates.All(c=>!c.PairedRims),"Transition finder accepted "+invalid);Console.WriteLine("PASS transition "+invalid+" rejected");
  }
 }
}
