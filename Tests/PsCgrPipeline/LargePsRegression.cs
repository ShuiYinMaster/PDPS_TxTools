using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using TxTools.ExportByColor;

class LargePsRegression
{
    static int Main(string[] args)
    {
        try
        {
            int geometries=args.Length>1?int.Parse(args[1]):12711;
            var groups=new List<ColorGroup>();long triangles=0;
            for(int shape=0;shape<geometries;shape++)
            {
                var group=new ColorGroup{Surface=shape+1,R=160,G=170,B=180};float origin=shape*100;
                for(int cell=0;cell<43;cell++)
                {
                    float x=origin+cell;
                    group.Tris.Add(new[]{x,0,0,x+1,0,0,x+1,1,0});
                    group.Tris.Add(new[]{x,0,0,x+1,1,0,x,1,0});
                }
                if(shape<9267)group.Tris.Add(new[]{origin+43,0,0,origin+44,0,0,origin+43,1,0});
                triangles+=group.Tris.Count;groups.Add(group);
            }
            Console.WriteLine("INPUT geometries="+geometries+" triangles="+triangles);
            var watch=Stopwatch.StartNew();
            var result=PsCgrPipeline.BuildFile(groups,args[0],CgrBackend.LineFace,true,Console.WriteLine);
            if(result.Cylinders!=0)throw new Exception("Planar geometry misclassified as cylinders");
            string summary="PASS large PS snapshot geometries="+geometries+" triangles="+triangles+" elapsed_s="+watch.Elapsed.TotalSeconds.ToString("F2")+" bytes="+new FileInfo(args[0]).Length;
            Console.WriteLine(summary);File.WriteAllText(args[0]+".timing.txt",summary);return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
