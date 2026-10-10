using System;using System.IO;using System.Collections.Generic;using System.Reflection;using System.Runtime.InteropServices;using TxTools.ExportByColor;
class FeatureAlphaProbe {
 [STAThread]static int Main(string[] args){AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{var p=Path.Combine(@"E:\ProcessSimulatePlugin\Process Simulate\bin",new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};try{Run(args[0]);return 0;}catch(Exception e){Console.WriteLine(e);return 1;}}
 static void Run(string dir){Directory.CreateDirectory(dir);
  var vs=new List<float[]>{new[]{-100f,-100f,0},new[]{100f,-100f,0},new[]{100f,100f,0},new[]{-100f,100f,0},new[]{-100f,-100f,-10},new[]{100f,-100f,-10},new[]{100f,100f,-10},new[]{-100f,100f,-10}};
  var fs=new List<CgrWriter.Face>{new CgrWriter.Face{Idx=new[]{0,1,2},R=255,Surface=1,Nz=1},new CgrWriter.Face{Idx=new[]{0,2,3},R=255,Surface=1,Nz=1},new CgrWriter.Face{Idx=new[]{4,5,6},B=255,Surface=2,Nz=1},new CgrWriter.Face{Idx=new[]{4,6,7},B=255,Surface=2,Nz=1}};
  INFITF.Application app=(INFITF.Application)Marshal.GetActiveObject("CATIA.Application");
  foreach(var mode in new[]{CgrBackend.LineFace,CgrBackend.LineFacePlanar})foreach(byte alpha in new byte[]{255,128}){
   for(int i=0;i<2;i++){var f=fs[i];f.Opacity=alpha;fs[i]=f;}
   string name=mode+"_"+alpha,file=Path.Combine(dir,name+".cgr");CgrWriter.BuildFile(vs,fs,file,mode,20000,null,true);
   var doc=(ProductStructureTypeLib.ProductDocument)app.Documents.Add("Product");doc.Product.set_PartNumber(name);doc.Product.Products.AddComponentsFromFiles(new object[]{file},"All");
   ((INFITF.SpecsAndGeomWindow)app.ActiveWindow).Layout=INFITF.CatSpecsAndGeomWindowLayout.catWindowGeomOnly;var viewer=(INFITF.Viewer3D)app.ActiveWindow.ActiveViewer;viewer.RenderingMode=INFITF.CatRenderingMode.catRenderShading;
   var vp=viewer.Viewpoint3D;vp.PutSightDirection(new object[]{0.0,0.0,-1.0});vp.PutUpDirection(new object[]{0.0,1.0,0.0});viewer.Viewpoint3D=vp;viewer.Reframe();viewer.Update();viewer.CaptureToFile(INFITF.CatCaptureFormat.catCaptureFormatBMP,Path.Combine(dir,name+".bmp"));Console.WriteLine("PROBE="+name);doc.Close();
  }
 }
}