using System;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using TxTools.ExportByColor;
class ParallelRegression
{
    static string Root;
    static int active, peak;
    static int Main(string[] args)
    {
        if(args.Length!=4) { Console.Error.WriteLine("Usage: ParallelRegression robot.jt gun.jt new-output-directory production-bin"); return 1; }
        Root=Path.GetFullPath(args[3]);
        AppDomain.CurrentDomain.AssemblyResolve += (s,e) => {
            foreach (var dir in new[]{Root,Path.Combine(Path.GetDirectoryName(Root),"2402dll")}) {
                var p=Path.Combine(dir,new AssemblyName(e.Name).Name+".dll");
                if(File.Exists(p))return Assembly.LoadFrom(p);
            } return null;
        };
        try { Run(args); return 0; } catch(Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    static string Hash(string p) { using(var s=File.OpenRead(p)) using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(s)); }
    static double[] Placement(int i) { return new double[]{1,0,0,0,0,1,0,0,0,0,1,0,i*100,0,0,1}; }
    static Task<Exception> Convert(string source,string directory,int i)
    {
        return Task.Run(() => {
            int count=Interlocked.Increment(ref active);
            int old; do { old=peak; if(count<=old)break; }while(Interlocked.CompareExchange(ref peak,count,old)!=old);
            try {
                JtDirectBridge.ConvertToCgr(source,directory,Path.Combine(Root,"JtDirectCs","TxTools.JtDecoder.exe"),
                    Path.Combine(directory,"sample_"+i+".cgr"),Placement(i),null);
                return (Exception)null;
            } catch(Exception e) { return e; }
            finally { Interlocked.Decrement(ref active); }
        });
    }
    static long Batch(string[] sources,string directory,int workers,bool expectFailure=false)
    {
        Directory.CreateDirectory(directory); active=peak=0;
        var pending=new Queue<Task<Exception>>(); int failures=0;
        var time=Stopwatch.StartNew();
        for(int i=0;i<sources.Length;i++) {
            if(pending.Count>=workers && pending.Dequeue().GetAwaiter().GetResult()!=null)failures++;
            pending.Enqueue(Convert(sources[i],directory,i));
        }
        while(pending.Count>0)if(pending.Dequeue().GetAwaiter().GetResult()!=null)failures++;
        time.Stop();
        if(peak>workers || active!=0 || failures!=(expectFailure?1:0))throw new Exception("Bound/failure regression");
        Console.WriteLine("BATCH workers="+workers+" peak="+peak+" failed="+failures+" ms="+time.ElapsedMilliseconds);
        if(workers==2 && peak!=2)throw new Exception("No actual overlap");
        return time.ElapsedMilliseconds;
    }
    static void Run(string[] args)
    {
        string previous=Environment.GetEnvironmentVariable("TXTOOLS_JT_WORKERS");
        try {
            Environment.SetEnvironmentVariable("TXTOOLS_JT_WORKERS","1"); if(JtDirectBridge.ConversionWorkers!=1)throw new Exception("Serial override");
            Environment.SetEnvironmentVariable("TXTOOLS_JT_WORKERS","999"); if(JtDirectBridge.ConversionWorkers>4)throw new Exception("Unbounded override");
            Environment.SetEnvironmentVariable("TXTOOLS_JT_WORKERS",null);
            Console.WriteLine("DEFAULT_WORKERS="+JtDirectBridge.ConversionWorkers+" CPU="+Environment.ProcessorCount);
        } finally { Environment.SetEnvironmentVariable("TXTOOLS_JT_WORKERS",previous); }
        string[] sources={args[0],args[1],args[0],args[1]};
        string serial=Path.Combine(args[2],"serial"),parallel=Path.Combine(args[2],"parallel");
        long one=Batch(sources,serial,1),two=Batch(sources,parallel,2);
        for(int i=0;i<sources.Length;i++) {
            if(Hash(Path.Combine(serial,"sample_"+i+".cgr"))!=Hash(Path.Combine(parallel,"sample_"+i+".cgr")))
                throw new Exception("Parallel geometry/material/placement differs at "+i);
        }
        Batch(new[]{args[0],Path.Combine(args[2],"missing.jt"),args[1]},Path.Combine(args[2],"failure"),2,true);
        if(!File.Exists(Path.Combine(args[2],"failure","sample_2.cgr")))throw new Exception("Failure stopped subsequent resource");
        Console.WriteLine("PASS byte-identical CGRs for four placed resources; failure isolated; serial_ms="+one+" parallel_ms="+two+" speedup="+((double)one/two).ToString("F2"));
    }
}
