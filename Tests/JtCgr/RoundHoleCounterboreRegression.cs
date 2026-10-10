using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using TxTools.ExportByColor;

class RoundHoleCounterboreRegression
{
    static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{var p=Path.Combine(args[0],new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
        try{Run();return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
    static void Run()
    {
        const int n=10;var vertices=new List<float[]>();var faces=new List<CgrWriter.Face>();
        double[] radius={20,10,10,5,5,20},height={0,0,8,8,15,15};
        for(int ring=0;ring<6;ring++)for(int i=0;i<n;i++){double a=2*Math.PI*i/n;vertices.Add(new[]{(float)(radius[ring]*Math.Cos(a)),(float)(radius[ring]*Math.Sin(a)),(float)height[ring]});}
        Action<int,int,int> add=(a,b,c)=>faces.Add(new CgrWriter.Face{Idx=new[]{a,b,c},Surface=1,R=10,G=20,B=30,Opacity=127});
        Action<int,int,bool> band=(first,last,reverse)=>{for(int i=0;i<n;i++){int j=(i+1)%n,a=first*n+i,b=first*n+j,c=last*n+j,d=last*n+i;if(reverse){add(a,c,b);add(a,d,c);}else{add(a,b,c);add(a,c,d);}}};
        band(1,0,false);band(1,2,true);band(3,2,false);band(3,4,true);band(4,5,true);band(0,5,false);
        var holes=RoundHoleReconstruction.Find(vertices,faces);var rebuilt=RoundHoleCgrMesh.Replace(vertices,faces,holes,Console.WriteLine);
        if(rebuilt.Cylinders.Count!=2||rebuilt.RemovedWallTriangles!=40)throw new Exception("Ten-point counterbore must reconstruct both cylindrical levels");
        var radii=rebuilt.Cylinders.Values.Select(c=>c.Radius).OrderBy(r=>r).ToArray();
        if(Math.Abs(radii[0]-5)>.001||Math.Abs(radii[1]-10)>.001)throw new Exception("Counterbore dimensions changed");
        // A ray just inside the smaller fitted circle must pass both planar caps.
        // The original ten-sided smaller hole blocks this ray near its side centers.
        for(int sample=0;sample<100;sample++)
        {
            double angle=(sample+.5)*2*Math.PI/100,x=4.9*Math.Cos(angle),y=4.9*Math.Sin(angle);
            foreach(var f in rebuilt.Faces.Where(f=>f.Surface==1))
            {
                var a=rebuilt.Vertices[f.Idx[0]];var b=rebuilt.Vertices[f.Idx[1]];var c=rebuilt.Vertices[f.Idx[2]];
                double d=(b[1]-c[1])*(a[0]-c[0])+(c[0]-b[0])*(a[1]-c[1]);if(Math.Abs(d)<1e-12)continue;
                double u=((b[1]-c[1])*(x-c[0])+(c[0]-b[0])*(y-c[1]))/d;
                double v=((c[1]-a[1])*(x-c[0])+(a[0]-c[0])*(y-c[1]))/d;
                if(u>=0&&v>=0&&u+v<=1)throw new Exception("An original polygonal cap still obstructs the circular hole");
            }
        }
        if(rebuilt.Faces.Any(f=>f.R!=10||f.G!=20||f.B!=30||f.Opacity!=127))throw new Exception("Counterbore RGBA changed");
        Console.WriteLine("PASS ten-point stepped bore; both cylinder walls and common annular cap rebuilt; circular through-hole silhouette and RGBA preserved");
    }
}
