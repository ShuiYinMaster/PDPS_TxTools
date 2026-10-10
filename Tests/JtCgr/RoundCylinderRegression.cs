using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using TxTools.ExportByColor;

class RoundCylinderRegression
{
    static void Assert(bool ok,string message){if(!ok)throw new Exception(message);}
    static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{var p=Path.Combine(args[0],new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
        try{foreach(int segments in new[]{8,16})foreach(bool solid in new[]{false,true})foreach(bool rotated in new[]{false,true})Run(segments,solid,rotated);return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
    static string Point(float[] p){return string.Join(",",p.Select(x=>x.ToString("R",System.Globalization.CultureInfo.InvariantCulture)));}
    static void Run(int n,bool solid,bool rotated)
    {
        var vertices=new List<float[]>();var faces=new List<CgrWriter.Face>();
        // Outer and inner walls share annular caps in one original shape.
        double[] radii={20,20,10,10};double[] z={0,15,0,15};
        for(int ring=0;ring<4;ring++)for(int i=0;i<n;i++){double a=2*Math.PI*i/n;vertices.Add(new[]{(float)(radii[ring]*Math.Cos(a)),(float)(radii[ring]*Math.Sin(a)),(float)z[ring]});}
        Action<int,int,int> add=(a,b,c)=>faces.Add(new CgrWriter.Face{Idx=new[]{a,b,c},Surface=1,R=10,G=20,B=30,Opacity=127});
        for(int i=0;i<n;i++){int j=(i+1)%n;add(i,j,n+j);add(i,n+j,n+i);}
        if(solid){int bottom=vertices.Count;vertices.Add(new[]{0f,0f,0f});int top=vertices.Count;vertices.Add(new[]{0f,0f,15f});for(int i=0;i<n;i++){int j=(i+1)%n;add(bottom,j,i);add(top,n+i,n+j);}}
        else for(int i=0;i<n;i++){int j=(i+1)%n;add(2*n+i,3*n+j,2*n+j);add(2*n+i,3*n+i,3*n+j);add(i,2*n+j,j);add(i,2*n+i,2*n+j);add(n+i,n+j,3*n+j);add(n+i,3*n+j,3*n+i);}
        if(rotated)foreach(var p in vertices){double x=p[0],y=p[1],h=p[2];p[0]=(float)(10000+.8*x+.36*y+.48*h);p[1]=(float)(20000+.6*x-.48*y-.64*h);p[2]=(float)(30000+.8*y-.6*h);}
        var before=vertices.Select(p=>(float[])p.Clone()).ToList();var originalFaces=faces.Select(f=>(int[])f.Idx.Clone()).ToList();
        var defaults=RoundHoleReconstruction.Find(vertices,faces);Assert(defaults.Count(h=>h.PairedRims)==(solid?0:1),"Default hole finder included an external wall or missed the coarse bore");
        var candidates=RoundHoleReconstruction.Find(vertices,faces,new RoundHoleReconstruction.Options{IncludeExternalCylinders=true});
        var result=RoundHoleCgrMesh.Replace(vertices,faces,candidates,Console.WriteLine);
        Assert(result.Cylinders.Count==(solid?1:2),"Closed cylinder walls not rebuilt");
        Assert(result.Cylinders.Values.Count(c=>c.ExternalCylinder)==1,"External wall classification changed");
        Assert(result.Faces.All(f=>f.R==10&&f.G==20&&f.B==30&&f.Opacity==127),"RGBA changed");
        Assert(vertices.Select((p,i)=>p.SequenceEqual(before[i])).All(b=>b)&&faces.Select((f,i)=>f.Idx.SequenceEqual(originalFaces[i])).All(b=>b),"Source mesh changed");
        foreach(var cylinder in result.Cylinders.Values)
        {
            Assert(Math.Abs(cylinder.Radius-(cylinder.ExternalCylinder?20:10))<.005&&Math.Abs(cylinder.Depth-15)<.005,"Cylinder dimensions changed");
            foreach(var face in result.Faces.Where(f=>f.Surface==cylinder.Surface))
            {
                var a=result.Vertices[face.Idx[0]];var b=result.Vertices[face.Idx[1]];var c=result.Vertices[face.Idx[2]];
                double[] ab={b[0]-a[0],b[1]-a[1],b[2]-a[2]},ac={c[0]-a[0],c[1]-a[1],c[2]-a[2]};
                double[] normal={ab[1]*ac[2]-ab[2]*ac[1],ab[2]*ac[0]-ab[0]*ac[2],ab[0]*ac[1]-ab[1]*ac[0]};
                var radial=Enumerable.Range(0,3).Select(k=>(double)a[k]-cylinder.Center[k]).ToArray();double axial=radial.Select((x,k)=>x*cylinder.Axis[k]).Sum();
                double facing=normal.Select((x,k)=>x*(radial[k]-axial*cylinder.Axis[k])).Sum();Assert(cylinder.ExternalCylinder?facing>0:facing<0,"Inner/outer winding inverted");
            }
        }
        // Every edge of this closed model must be shared by exactly two triangles,
        // including all inserted circular cap/wall joins (weld by exact XYZ).
        var edges=new Dictionary<string,int>();foreach(var f in result.Faces)for(int k=0;k<3;k++){string a=Point(result.Vertices[f.Idx[k]]),b=Point(result.Vertices[f.Idx[(k+1)%3]]);string key=string.CompareOrdinal(a,b)<0?a+"|"+b:b+"|"+a;int count;edges.TryGetValue(key,out count);edges[key]=count+1;}
        Assert(edges.Values.All(count=>count==2),"Rebuilt caps/walls contain cracks or duplicate edges");
        Console.WriteLine("PASS "+n+"-segment "+(solid?"solid external cylinder":"shared-cap inner/outer tube")+" "+(rotated?"arbitrary axis at large coordinates":"axis Z")+"; closed mesh, winding, dimensions, RGBA and source immutability");
        if(solid&&!rotated&&n==8)
        {
            foreach(string invalid in new[]{"ellipse","cone","open"})
            {
                var badVertices=before.Select(p=>(float[])p.Clone()).ToList();var badFaces=new List<CgrWriter.Face>(faces);
                if(invalid=="ellipse")foreach(var p in badVertices)p[0]*=1.2f;
                if(invalid=="cone")foreach(var p in badVertices)if(p[2]>14){p[0]*=1.25f;p[1]*=1.25f;}
                if(invalid=="open")badFaces.RemoveAt(0);
                var found=RoundHoleReconstruction.Find(badVertices,badFaces,new RoundHoleReconstruction.Options{IncludeExternalCylinders=true});
                Assert(found.All(h=>!h.PairedRims),"External cylinder finder accepted "+invalid);
                Console.WriteLine("PASS external "+invalid+" rejected");
            }
        }
    }
}
