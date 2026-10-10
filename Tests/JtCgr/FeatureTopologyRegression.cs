using System;using System.IO;using System.Collections.Generic;using System.Linq;using System.Reflection;using TxTools.ExportByColor;
class FeatureTopologyRegression {
 static string root;
 static List<CgrWriter.Face> Decode(string path,out List<float[]> vertices){var data=File.ReadAllBytes(path);vertices=new List<float[]>();var faces=new List<CgrWriter.Face>();int leaf=0;for(int p=0;p+46<=data.Length;){if(data[p]!=0x95||data[p+1]!=255||data[p+41]!=255||data[p+42]!=255||data[p+43]!=2||data[p+44]!=0||data[p+45]!=1){p++;continue;}int end=checked(p+(int)BitConverter.ToUInt32(data,p+2)+1);typeof(FeatureTestDecoder).GetMethod("ReadLeaf",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{data,p+41,end,leaf++,vertices,faces});p=end;}return faces;}
 static int Main(string[] args){root=args[0];AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{var p=Path.Combine(root,new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};try{Run(args[1]);return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
 static void Run(string dir){Directory.CreateDirectory(dir);foreach(var mode in new[]{CgrBackend.LineFace,CgrBackend.LineFacePlanar}){
  var vs=new List<float[]>();var fs=new List<CgrWriter.Face>();byte[] alpha={0,128,255,255};
  for(int i=0;i<4;i++){vs.Add(new[]{0f,0f,(float)i});vs.Add(new[]{i==3?1e8f:1f,0f,(float)i});vs.Add(new[]{i==3?1e8f:0f,i==3?1e-6f:1f,(float)i});fs.Add(new CgrWriter.Face{Idx=new[]{3*i,3*i+1,3*i+2},R=255,Opacity=alpha[i],Surface=1,Nz=1});}
  string output=Path.Combine(dir,mode+"_alpha.cgr");CgrWriter.BuildFile(vs,fs,output,mode,20000,null,true);List<float[]> points;var decoded=Decode(output,out points);var counts=decoded.GroupBy(f=>f.Opacity??255).ToDictionary(g=>g.Key,g=>g.Count());if(counts.Count!=3||counts[0]!=1||counts[128]!=1||counts[255]!=2)throw new Exception("Alpha grouping or slender triangle lost");
  // Two coincident sheets have distinct vertex identities. They must not gain a neighbor.
  vs=new List<float[]>();fs=new List<CgrWriter.Face>();for(int i=0;i<2;i++){int n=vs.Count;vs.AddRange(new[]{new[]{0f,0f,0f},new[]{1f,0f,0f},new[]{1f,1f,0f},new[]{0f,1f,0f}});fs.Add(new CgrWriter.Face{Idx=new[]{n,n+1,n+2},R=255,Surface=1,Nz=1});fs.Add(new CgrWriter.Face{Idx=new[]{n,n+2,n+3},R=255,Surface=1,Nz=1});}
  FeatureTestDecoder.DomainCount=0;FeatureTestDecoder.EdgeCount=0;output=Path.Combine(dir,mode+"_sheets.cgr");CgrWriter.BuildFile(vs,fs,output,mode,20000,null,true);decoded=Decode(output,out points);if(decoded.Count!=4||FeatureTestDecoder.DomainCount!=2||FeatureTestDecoder.EdgeCount!=8)throw new Exception("Coincident source sheets were welded");Console.WriteLine("PASS "+mode+" alpha 0/128/255, slender triangle, separate coincident sheets, two domains/eight boundaries");
 }}
}
