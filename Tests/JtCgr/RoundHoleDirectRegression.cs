using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using TxTools.ExportByColor;

class RoundHoleDirectRegression
{
    static string bin;
    [STAThread] static int Main(string[] args)
    {
        if(args.Length<3){Console.Error.WriteLine("source.jt output-directory production-bin [independently-verified.cgr]");return 2;}
        bin=args[2];AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{var p=Path.Combine(bin,new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
        try{Run(args);return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
    static string Hash(string p){using(var h=SHA256.Create())using(var s=File.OpenRead(p))return BitConverter.ToString(h.ComputeHash(s));}
    static void Run(string[] args)
    {
        string dir=Path.GetFullPath(args[1]);Directory.CreateDirectory(dir);string cgr=Path.Combine(dir,"fixture.cgr");
        var holes=JtDirectBridge.ConvertToCgrWithHoles(args[0],dir,Path.Combine(bin,"JtDirectCs","TxTools.JtDecoder.exe"),cgr,
            new double[]{1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1},CgrBackend.LineFace,Console.WriteLine);
        if(holes.Count==0||File.ReadAllLines(cgr+".holes.csv").Length<holes.Count+1)throw new Exception("Missing fitted hole sidecar");
        if(args.Length>3&&Hash(cgr)!=Hash(args[3]))throw new Exception("Production entry differs from independently read-back CGR");
        var app=(INFITF.Application)Marshal.GetActiveObject("CATIA.Application");app.Visible=true;app.Width=1800;app.Height=1000;
        var doc=(ProductStructureTypeLib.ProductDocument)app.Documents.Add("Product");
        doc.Product.set_PartNumber("JT_CGR_NATIVE_HOLES_"+DateTime.Now.ToString("HHmmss"));
        doc.Product.Products.AddComponentsFromFiles(new object[]{cgr},"All");
        if(doc.Product.Products.Count!=1)throw new Exception("Expected one CGR component");
        app.ActiveWindow.WindowState=INFITF.CatWindowState.catWindowStateMaximized;
        ((INFITF.SpecsAndGeomWindow)app.ActiveWindow).Layout=INFITF.CatSpecsAndGeomWindowLayout.catWindowGeomOnly;
        doc.SaveAs(Path.Combine(dir,"validation.CATProduct"));
        var viewer=(INFITF.Viewer3D)app.ActiveWindow.ActiveViewer;
        viewer.RenderingMode=INFITF.CatRenderingMode.catRenderShadingWithEdges;viewer.Reframe();viewer.Update();
        viewer.CaptureToFile(INFITF.CatCaptureFormat.catCaptureFormatBMP,Path.Combine(dir,"validation.bmp"));
        Console.WriteLine("PASS production JT decoder -> single native canonical CGR -> CATIA; replaced="+holes.Count+"; no CATPart, auxiliary axis, or PS geometry capture; review left open");
    }
}
