using System;using System.IO;using System.Linq;using System.Reflection;using System.Collections;using System.Collections.Generic;using System.Security.Cryptography;using TxTools.ExportByColor;
class RgbaCompatibilityRegression {
 static string root;
 static object F(object o,string n){return o.GetType().GetField(n).GetValue(o);}
 static string Hash(string p){using(var s=File.OpenRead(p))using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(s));}
 static int Main(string[] args){root=args[0];AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{var p=Path.Combine(root,new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};try{Run(args);return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
 static void Run(string[] args){Directory.CreateDirectory(args[1]);
  var vs=new List<float[]>{new[]{0f,0f,0f},new[]{1f,0f,0f},new[]{0f,1f,0f},new[]{0f,0f,1f},new[]{1f,0f,1f},new[]{0f,1f,1f},new[]{0f,0f,2f},new[]{1f,0f,2f},new[]{0f,1f,2f},new[]{0f,0f,3f},new[]{1e8f,0f,3f},new[]{1e8f,1e-6f,3f}};
  var fs=new List<CgrWriter.Face>();byte[] alpha={0,128,255,255};for(int i=0;i<4;i++)fs.Add(new CgrWriter.Face{Idx=new[]{i*3,i*3+1,i*3+2},R=255,Opacity=alpha[i],Surface=1,Nz=1});
  string output=Path.Combine(args[1],"same_rgb_skinny.cgr");CgrWriter.BuildFile(vs,fs,output,CgrBackend.Compact,20000,null,true);
  var data=File.ReadAllBytes(output);var counts=new Dictionary<byte,int>();var t=typeof(Cfv3Encoder);
  foreach(var r in (IEnumerable)t.GetMethod("FindLeaves",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{data})){
   int end=(int)F(r,"DataEnd");byte a=data[end-5];var copy=(byte[])data.Clone();copy[end-5]=255;
   var leaf=t.GetMethod("ParseLeaf",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{copy,r});int n=((int[])F(leaf,"Indices")).Length/3;int old;counts.TryGetValue(a,out old);counts[a]=old+n;
  }
  if(counts.Count!=3||counts[0]!=1||counts[128]!=1||counts[255]!=2)throw new Exception("Opacity groups merged or skinny triangle lost");
  try { CgrWriter.BuildFile(vs,fs,output+".unsupported",CgrBackend.Legacy,20000,null,true);throw new Exception("Unsupported backend silently dropped alpha"); }catch(NotSupportedException){}
  var mesh=JtDirectBridge.Read(args[2],args[3]);if(mesh.Faces.Any(f=>f.Alpha!=255))throw new Exception("Legacy mesh opacity changed");
  for(int i=0;i<4;i++){string f="sample_"+i+".cgr";if(Hash(Path.Combine(args[4],f))!=Hash(Path.Combine(args[5],f)))throw new Exception("Opaque production output changed: "+f);}
  Console.WriteLine("PASS opacity separation, alpha zero, slender triangle, unsupported backend guard, JTMESH01 read compatibility, four opaque CGRs byte-identical to previous production");
 }
}
