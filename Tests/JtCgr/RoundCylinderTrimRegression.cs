using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using TxTools.ExportByColor;

class RoundCylinderTrimRegression
{
    static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
    static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{string path=Path.Combine(args[0],new AssemblyName(e.Name).Name+".dll");return File.Exists(path)?Assembly.LoadFrom(path):null;};
        try{Run();return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
    static RoundHoleReconstruction.Options Options(){return new RoundHoleReconstruction.Options{IncludeExternalCylinders=true,IncludeTransitionRims=true,IncludeOpenCylinderEnds=true,IncludeTrimmedCylinderEnds=true};}
    static double Height(int ring,double angle,bool both){return ring==0?.09*Math.Cos(2*angle):(both?17+.2*Math.Sin(2*angle):17);}
    static void Mesh(bool outer,bool both,int cap,List<float[]> v,List<CgrWriter.Face> f)
    {
        const int n=16;
        for(int ring=0;ring<2;ring++)for(int i=0;i<n;i++){double a=2*Math.PI*i/n;v.Add(new[]{(float)(5*Math.Cos(a)),(float)(5*Math.Sin(a)),(float)Height(ring,a,both)});}
        for(int i=0;i<n;i++){int j=(i+1)%n;foreach(var t in new[]{new[]{i,j,n+j},new[]{i,n+j,n+i}}){if(!outer){int swap=t[1];t[1]=t[2];t[2]=swap;}f.Add(new CgrWriter.Face{Idx=t,Surface=19,R=23,G=91,B=187,Opacity=128});}}
        if(cap==0)return;
        for(int i=0;i<n;i++){double a=2*Math.PI*i/n;v.Add(new[]{(float)(12*Math.Cos(a)),(float)(12*Math.Sin(a)),0f});}
        for(int i=0;i<n;i++){int j=(i+1)%n;foreach(var t in new[]{new[]{i,2*n+j,2*n+i},new[]{i,j,2*n+j}})f.Add(new CgrWriter.Face{Idx=t,Surface=cap==1?19:20,R=cap==1?(byte)23:(byte)190,G=71,B=32,Opacity=255});}
    }
    static string Point(float[] p){return string.Join(",",p.Select(x=>x.ToString("R",System.Globalization.CultureInfo.InvariantCulture)));}
    static float[] Rotate(float[] p){return new[]{(float)(1000+.8*p[0]+.36*p[1]+.48*p[2]),(float)(2000+.6*p[0]-.48*p[1]-.64*p[2]),(float)(3000+.8*p[1]-.6*p[2])};}
    static void Run()
    {
        foreach(bool outer in new[]{false,true})foreach(bool both in new[]{false,true})foreach(bool rotate in new[]{false,true})Test(outer,both,rotate,0);
        Test(false,false,false,1);Test(false,false,false,2);Test(true,true,true,2);
        foreach(bool both in new[]{false,true})TestStaggered(both);
        foreach(string kind in new[]{"gap","cone","ellipse","duplicate","crossing"})Reject(kind);
    }
    static void TestStaggered(bool both)
    {
        // Unequal angular sampling biases geometric triangle normals. A known
        // planar end or two projected centers must recover the same cylinder.
        var v=new List<float[]>();var f=new List<CgrWriter.Face>();const int n=16;
        var lower=Enumerable.Range(0,n).Select(i=>2*Math.PI*i/n).ToList();
        var upper=new List<double>(lower){.3*2*Math.PI/n,Math.PI/2+.3*2*Math.PI/n};upper.Sort();
        foreach(double a in lower)v.Add(new[]{(float)(5*Math.Cos(a)),(float)(5*Math.Sin(a)),(float)(both?Height(0,a,true):0)});
        foreach(double a in upper)v.Add(new[]{(float)(5*Math.Cos(a)),(float)(5*Math.Sin(a)),(float)Height(1,a,true)});
        int first=0,second=0;
        while(first<n||second<upper.Count)
        {
            double nextFirst=first<n? (first+1<n?lower[first+1]:2*Math.PI):double.PositiveInfinity;
            double nextSecond=second<upper.Count? (second+1<upper.Count?upper[second+1]:2*Math.PI):double.PositiveInfinity;
            int a=first%n,b=n+second%upper.Count;int[] t;
            if(nextFirst<nextSecond){t=new[]{a,(first+1)%n,b};first++;}
            else{t=new[]{a,n+(second+1)%upper.Count,b};second++;}
            int swap=t[1];t[1]=t[2];t[2]=swap;f.Add(new CgrWriter.Face{Idx=t,Surface=19,R=23,G=91,B=187,Opacity=128});
        }
        var holes=RoundHoleReconstruction.Find(v,f,Options());var r=RoundHoleCgrMesh.Replace(v,f,holes);
        Check(r.Cylinders.Count==1&&r.RemovedWallTriangles==f.Count,"Unequal rim sampling missed the trimmed cylinder");
        var c=r.Cylinders.Values.Single();Check(Math.Abs(c.Axis[2])>.999999&&Math.Abs(c.Radius-5)<.001,"Facet-biased cylinder axis was retained");
        Console.WriteLine("PASS unequal 16/18 rim samples bothTrimmed="+both+"; full wall radial fit and center-line axis");
    }
    static void Test(bool outer,bool both,bool rotate,int cap)
    {
        var v=new List<float[]>();var f=new List<CgrWriter.Face>();Mesh(outer,both,cap,v,f);
        if(rotate)for(int i=0;i<v.Count;i++)v[i]=Rotate(v[i]);
        var original=v.Select(p=>(float[])p.Clone()).ToList();var originalTriangles=f.Select(p=>(int[])p.Idx.Clone()).ToList();
        var holes=RoundHoleReconstruction.Find(v,f,Options());Check(holes.Count==1&&holes[0].PairedRims,"Trimmed cylinder not paired");
        var h=holes[0];Check(h.TrimmedRimA||h.TrimmedRimB,"Nonplanar source end lost");Check(both? h.TrimmedRimA&&h.TrimmedRimB : h.TrimmedRimA!=h.TrimmedRimB,"Incorrect trimmed-end flags");
        var r=RoundHoleCgrMesh.Replace(v,f,holes,Console.WriteLine);Check(r.Cylinders.Count==1&&r.RemovedWallTriangles==32,"Trimmed wall rejected");
        var c=r.Cylinders.Values.Single();Check(c.ExternalCylinder==outer&&Math.Abs(c.Radius-5)<.001&&Math.Abs(c.Depth-17)<.001,"Cylinder parameters changed");
        var walls=r.Faces.Where(x=>x.Surface==c.Surface).ToList();Check(walls.All(x=>x.R==23&&x.G==91&&x.B==187&&x.Opacity==128),"Wall RGBA changed");
        foreach(int index in walls.SelectMany(x=>x.Idx).Distinct())
        {
            var p=r.Vertices[index];double x=p[0],y=p[1],z=p[2];
            if(rotate){x=.8*(p[0]-1000)+.6*(p[1]-2000);y=.36*(p[0]-1000)-.48*(p[1]-2000)+.8*(p[2]-3000);z=.48*(p[0]-1000)-.64*(p[1]-2000)-.6*(p[2]-3000);}
            Check(Math.Abs(Math.Sqrt(x*x+y*y)-5)<.001,"Generated wall is not cylindrical");
            double angle=Math.Atan2(y,x);int ring=z>8?1:0;double expected=SourceHeight(ring,angle,both);
            if(cap!=0&&ring==0)
            {
                var axis=rotate?new[]{.48,-.64,-.6}:new[]{0d,0d,1d};
                Check(f.Skip(32).Any(face=>{var a=v[face.Idx[0]];var b=v[face.Idx[1]];var d=v[face.Idx[2]];double[] ab={b[0]-a[0],b[1]-a[1],b[2]-a[2]},ad={d[0]-a[0],d[1]-a[1],d[2]-a[2]};double[] normal={ab[1]*ad[2]-ab[2]*ad[1],ab[2]*ad[0]-ab[0]*ad[2],ab[0]*ad[1]-ab[1]*ad[0]};double error=Math.Abs((p[0]-a[0])*normal[0]+(p[1]-a[1])*normal[1]+(p[2]-a[2])*normal[2]);return error/Math.Sqrt(normal.Sum(q=>q*q))<.001;}),"Cylinder/cap intersection left the source cap planes");
            }
            else Check(Math.Abs(z-expected)<.001,"Source open trim profile flattened or changed");
        }
        var counts=new Dictionary<string,int>();foreach(var face in r.Faces)for(int k=0;k<3;k++){string a=Point(r.Vertices[face.Idx[k]]),b=Point(r.Vertices[face.Idx[(k+1)%3]]),key=string.CompareOrdinal(a,b)<0?a+"|"+b:b+"|"+a;int n;counts.TryGetValue(key,out n);counts[key]=n+1;}
        Check(counts.Values.All(x=>x==1||x==2),"Nonmanifold output");Check(counts.Values.Count(x=>x==1)==(cap==0?r.AddedWallTriangles:r.AddedWallTriangles/2+16),"Trimmed rim join has cracks or invented caps");
        if(cap!=0){Check(r.ReplacedCapTriangles==16,"Source trimmed cap join omitted");Check(r.Faces.Where(x=>x.Surface==(cap==1?19:20)).All(x=>x.G==71&&x.B==32&&x.Opacity==255),"Cap RGBA changed");}
        foreach(int i in Enumerable.Range(0,v.Count))Check(v[i].SequenceEqual(original[i]),"Input vertex mutated");foreach(int i in Enumerable.Range(0,f.Count))Check(f[i].Idx.SequenceEqual(originalTriangles[i]),"Input triangle mutated");
        Console.WriteLine("PASS trimmed cylinder outer="+outer+" both="+both+" rotated="+rotate+" cap="+cap+"; trim heights, circular radial profile, dimensions, RGBA, seam topology");
    }
    static double SourceHeight(int ring,double angle,bool both)
    {
        double step=2*Math.PI/16;while(angle<0)angle+=2*Math.PI;int i=(int)Math.Floor(angle/step);double a=i*step,b=(i+1)*step;
        double ca=Math.Sin(angle-a),cb=Math.Sin(angle-b);double t=ca/(ca-cb);
        return (1-t)*Height(ring,a,both)+t*Height(ring,b,both);
    }
    static void Reject(string kind)
    {
        var v=new List<float[]>();var f=new List<CgrWriter.Face>();Mesh(false,true,0,v,f);
        if(kind=="gap")f.RemoveRange(0,2);if(kind=="duplicate")f.AddRange(f.ToArray());
        if(kind=="ellipse")foreach(var p in v)p[1]*=1.2f;
        if(kind=="cone")for(int i=16;i<32;i++){v[i][0]*=1.2f;v[i][1]*=1.2f;}
        if(kind=="crossing")for(int i=16;i<32;i++)v[i][2]=(float)(.03+.2*Math.Cos(2*Math.PI*i/16));
        var h=RoundHoleReconstruction.Find(v,f,Options());var r=RoundHoleCgrMesh.Replace(v,f,h);Check(r.Cylinders.Count==0,"Invalid trimmed wall accepted: "+kind);Console.WriteLine("PASS trimmed guard "+kind);
    }
}
