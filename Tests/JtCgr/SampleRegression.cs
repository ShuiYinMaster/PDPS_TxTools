using System;using System.IO;using System.Linq;using System.Collections;using System.Collections.Generic;using System.Reflection;using System.Globalization;using TxTools.ExportByColor;
class SampleRegression {
 static string Root;
 static object F(object o,string n){return o.GetType().GetField(n).GetValue(o);}
 static string Point(float[] p){return string.Join(",",p.Select(v=>v.ToString("R",CultureInfo.InvariantCulture)));}
 static string Key(string[] p,string rgba){int inv=0;for(int a=0;a<3;a++)for(int b=a+1;b<3;b++)if(StringComparer.Ordinal.Compare(p[a],p[b])>0)inv++;Array.Sort(p,StringComparer.Ordinal);return rgba+":"+inv%2+":"+string.Join(";",p);}
 static void Add(Dictionary<string,int> d,string k,int change){int v;d.TryGetValue(k,out v);v+=change;if(v==0)d.Remove(k);else d[k]=v;}
 static int Main(string[] args){if(args.Length!=3){Console.Error.WriteLine("Usage: SampleRegression source.jt new-output-directory production-bin");return 1;}Root=Path.GetFullPath(args[2]);AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{var p=Path.Combine(Root,new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};try{Run(args);return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
 static void Run(string[] args){Directory.CreateDirectory(args[1]);string output=Path.Combine(args[1],"fixture.cgr");var timer=System.Diagnostics.Stopwatch.StartNew();
  JtDirectBridge.ConvertToCgr(args[0],args[1],Path.Combine(Root,"JtDirectCs","TxTools.JtDecoder.exe"),output,new double[]{1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1},Console.WriteLine);
  var mesh=JtDirectBridge.Read(Directory.GetFiles(args[1],"*.jtmesh").Single(),args[0]);
  Console.WriteLine("MESH vertices="+mesh.Vertices.Count+" faces="+mesh.Faces.Count+" shapes="+mesh.Faces.Select(f=>f.Surface).Distinct().Count()+" RGBA="+mesh.Faces.Select(f=>f.R+","+f.G+","+f.B+","+f.Alpha).Distinct().Count());
  foreach(var g in mesh.Faces.GroupBy(f=>f.Alpha).OrderBy(g=>g.Key))Console.WriteLine("ALPHA="+g.Key+" faces="+g.Count()+" shapes="+g.Select(f=>f.Surface).Distinct().Count());
  var delta=new Dictionary<string,int>();var points=mesh.Vertices.Select(Point).ToArray();
  foreach(var f in mesh.Faces)Add(delta,Key(f.Indices.Select(i=>points[i]).ToArray(),BitConverter.ToString(new[]{f.R,f.G,f.B,f.Alpha})),1);
  var t=typeof(Cfv3Encoder);var data=File.ReadAllBytes(output);var normalized=(byte[])data.Clone();int actual=0,leaves=0;
  foreach(var record in (IEnumerable)t.GetMethod("FindLeaves",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{data})){
   int end=(int)F(record,"DataEnd");byte alpha=data[end-5]; // color record: 20 04 04 alpha FF B G R
   if(data[end-8]!=32||data[end-7]!=4||data[end-6]!=4||data[end-4]!=255)throw new Exception("Bad RGBA record");
   normalized[end-5]=255; // Historical parser accepts opaque records; geometry grammar is identical.
   var leaf=t.GetMethod("ParseLeaf",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{normalized,record});var raw=(byte[])F(leaf,"RawCoordinates");var ids=(int[])F(leaf,"Indices");var c=(byte[])F(leaf,"ColorRecord");string rgba=BitConverter.ToString(new[]{c[7],c[6],c[5],alpha});
   var pp=new string[raw.Length/12];for(int i=0;i<pp.Length;i++)pp[i]=Point(new[]{BitConverter.ToSingle(raw,i*12),BitConverter.ToSingle(raw,i*12+4),BitConverter.ToSingle(raw,i*12+8)});
   for(int i=0;i<ids.Length;i+=3){Add(delta,Key(new[]{pp[ids[i]],pp[ids[i+1]],pp[ids[i+2]]},rgba),-1);actual++;}leaves++;
  }
  Console.WriteLine("READBACK triangles="+actual+" leaves="+leaves+" XYZ_WINDING_RGBA_DIFFERENCES="+delta.Count+" elapsed_ms="+timer.ElapsedMilliseconds);
  if(delta.Count!=0||actual!=mesh.Faces.Count)throw new Exception("RGBA geometry mismatch");

  Console.WriteLine("PASS direct JT -> RGBA CGR; complete geometry/color/opacity retained");
 }
}
