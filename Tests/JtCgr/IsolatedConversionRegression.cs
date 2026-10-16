using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TxTools.ExportByColor;

internal static class IsolatedConversionRegression
{
    private static string Bin;
    private static int Active, Peak;
    private sealed class Job { internal int Index; internal Task<Exception> Work; }
    private static int Main(string[] args)
    {
        if(args.Length!=4) { Console.Error.WriteLine("Usage: IsolatedConversionRegression gun95.jt gun106.jt output-dir production-bin"); return 1; }
        Bin=Path.GetFullPath(args[3]);
        AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=> {
            string p=Path.Combine(Bin,new AssemblyName(e.Name).Name+".dll"); return File.Exists(p)?Assembly.LoadFrom(p):null;
        };
        try { return Run(args); } catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int Run(string[] args)
    {
        string directory=Path.GetFullPath(args[2]); Directory.CreateDirectory(directory);
        string bad=Path.Combine(directory,"malformed.jt"); File.WriteAllText(bad,"Version 10.6 JT truncated fixture",Encoding.ASCII);
        string[] sources={args[0],bad,args[1],args[1]}; var pending=new List<Job>(); int ok=0,failed=0;
        var watch=Stopwatch.StartNew();
        Action<Job> finish=job=> {
            var error=job.Work.GetAwaiter().GetResult();
            if(error==null)ok++; else {failed++; Console.WriteLine("EXPECTED FAILURE index="+job.Index+" "+error.Message);}
            Console.WriteLine("FINISHED index="+job.Index+" elapsedMs="+watch.ElapsedMilliseconds);
        };
        for(int index=0;index<sources.Length;index++)
        {
            if(pending.Count>=2)finish(JtBatchSafety.TakeCompleted(pending,j=>j.Work));
            int current=index;
            var task=Task.Run(()=> {
                int active=Interlocked.Increment(ref Active), old;
                do {old=Peak;if(active<=old)break;} while(Interlocked.CompareExchange(ref Peak,active,old)!=old);
                try {
                    double[] matrix={1,0,0,0,0,1,0,0,0,0,1,0,current*100,0,0,1};
                    int[] counts=JtCgrProcessBridge.Convert(sources[current],directory,Path.Combine(Bin,"JtDirectCs","TxTools.JtDecoder.exe"),
                        Path.Combine(directory,"resource_"+current+".cgr"),matrix,current==3?CgrBackend.Compact:CgrBackend.LineFace,current!=3,
                        message=> {if(message.StartsWith("[JT isolated") || message.StartsWith("[CGR] 编码器"))Console.WriteLine(message);});
                    Console.WriteLine("CYLINDERS index="+current+" inner="+counts[0]+" outer="+counts[1]);
                    return (Exception)null;
                } catch(Exception ex) {return ex;}
                finally {Interlocked.Decrement(ref Active);}
            });
            pending.Add(new Job{Index=current,Work=task});
        }
        while(pending.Count>0)finish(JtBatchSafety.TakeCompleted(pending,j=>j.Work));
        if(ok!=3 || failed!=1 || Active!=0 || Peak!=2 || File.Exists(Path.Combine(directory,"resource_1.cgr")))
            throw new Exception("Actual isolated batch failure/bound/publication regression");
        foreach(int index in new[]{0,2,3})if(!File.Exists(Path.Combine(directory,"resource_"+index+".cgr")))throw new Exception("Healthy CGR missing");
        Console.WriteLine("PASS actual JT 9.5 + malformed JT + JT 10.6 (line/face reconstruction + compact); complete conversion isolated; workers=2; healthy=3; failed=1; ms="+watch.ElapsedMilliseconds);
        return 0;
    }
}
