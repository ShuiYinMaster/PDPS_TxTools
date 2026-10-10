using System;using System.IO;using System.Linq;using System.Collections;using System.Collections.Generic;using System.Reflection;using System.Globalization;using TxTools.ExportByColor;
class FeatureRegression {
 static string Root;
 static object F(object o,string n){return o.GetType().GetField(n).GetValue(o);}
 static string Point(float[] p){return string.Join(",",p.Select(v=>v.ToString("R",CultureInfo.InvariantCulture)));}
 static string Key(string[] p,string rgba){int inv=0;for(int a=0;a<3;a++)for(int b=a+1;b<3;b++)if(StringComparer.Ordinal.Compare(p[a],p[b])>0)inv++;Array.Sort(p,StringComparer.Ordinal);return rgba+":"+inv%2+":"+string.Join(";",p);}
 static void Add(Dictionary<string,int> d,string k,int change){int v;d.TryGetValue(k,out v);v+=change;if(v==0)d.Remove(k);else d[k]=v;}
 static int Main(string[] args){if(args.Length!=3 && args.Length!=4){Console.Error.WriteLine("Usage: SampleRegression source.jt new-output-directory production-bin");return 1;}Root=Path.GetFullPath(args[2]);AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{var p=Path.Combine(Root,new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};try{Run(args);return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
 static void Run(string[] args){Directory.CreateDirectory(args[1]);string output=Path.Combine(args[1],"fixture.cgr");var timer=System.Diagnostics.Stopwatch.StartNew();
  JtDirectBridge.ConvertToCgr(args[0],args[1],Path.Combine(Root,"JtDirectCs","TxTools.JtDecoder.exe"),output,new double[]{1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1},(CgrBackend)Enum.Parse(typeof(CgrBackend),args.Length==4?args[3]:"LineFace"),Console.WriteLine);
  var mesh=JtDirectBridge.Read(Directory.GetFiles(args[1],"*.jtmesh").Single(),args[0]);
  Console.WriteLine("MESH vertices="+mesh.Vertices.Count+" faces="+mesh.Faces.Count+" shapes="+mesh.Faces.Select(f=>f.Surface).Distinct().Count()+" RGBA="+mesh.Faces.Select(f=>f.R+","+f.G+","+f.B+","+f.Alpha).Distinct().Count());
  foreach(var g in mesh.Faces.GroupBy(f=>f.Alpha).OrderBy(g=>g.Key))Console.WriteLine("ALPHA="+g.Key+" faces="+g.Count()+" shapes="+g.Select(f=>f.Surface).Distinct().Count());
  var delta=new Dictionary<string,int>();var points=mesh.Vertices.Select(Point).ToArray();
  foreach(var f in mesh.Faces)Add(delta,Key(f.Indices.Select(i=>points[i]).ToArray(),BitConverter.ToString(new[]{f.R,f.G,f.B,f.Alpha})),1);
  var data=File.ReadAllBytes(output);int actual=0,leaves=0;
  var vertices=new List<float[]>();var faces=new List<CgrWriter.Face>();
  for(int p=0;p+46<=data.Length;){
   if(data[p]!=0x95||data[p+1]!=255||data[p+41]!=255||data[p+42]!=255||data[p+43]!=2||data[p+44]!=0||data[p+45]!=1){p++;continue;}
   int end=checked(p+(int)BitConverter.ToUInt32(data,p+2)+1);
   typeof(FeatureTestDecoder).GetMethod("ReadLeaf",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{data,p+41,end,leaves++,vertices,faces});p=end;
  }
  var pp=vertices.Select(Point).ToArray();
  foreach(var f in faces){Add(delta,Key(f.Idx.Select(i=>pp[i]).ToArray(),BitConverter.ToString(new[]{f.R,f.G,f.B,f.Opacity??255})),-1);actual++;}
  Console.WriteLine("READBACK triangles="+actual+" leaves="+leaves+" XYZ_WINDING_RGBA_DIFFERENCES="+delta.Count+" elapsed_ms="+timer.ElapsedMilliseconds);
  if(delta.Count!=0||actual!=mesh.Faces.Count)throw new Exception("RGBA geometry mismatch");

  Console.WriteLine("PASS direct JT -> RGBA CGR; complete geometry/color/opacity retained");
 }
}
