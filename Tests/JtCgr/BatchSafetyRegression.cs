using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TxTools.ExportByColor;

internal static class BatchSafetyRegression
{
    private sealed class Node
    {
        internal string Id; internal bool Prototype, Backing;
        internal readonly List<Node> Kids = new List<Node>();
        internal Node(string id, bool prototype=false, bool backing=false) { Id=id; Prototype=prototype; Backing=backing; }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static string Exe { get { return Assembly.GetExecutingAssembly().Location; } }
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "fake") return Fake(args);
            Run(args.Length == 0 ? Path.Combine(Path.GetTempPath(), "JtBatchSafety_"+Guid.NewGuid().ToString("N")) : args[0]);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static int Fake(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        string mode=args[1], marker=args[2];
        if (mode == "grandchild") { Thread.Sleep(Timeout.Infinite); return 0; }
        while (!File.Exists(marker+".go")) Thread.Sleep(10);
        if (mode == "error") { Console.Error.WriteLine("fixture decoder failure"); return 13; }
        if (mode == "hang" || mode == "orphanpipe")
        {
            var child=Process.Start(new ProcessStartInfo(Exe,"fake grandchild "+JtProcessRunner.Quote(marker)) { UseShellExecute=false,CreateNoWindow=true });
            File.WriteAllText(marker+".pid",child.Id.ToString());
            if (mode == "orphanpipe") return 0;
            Thread.Sleep(Timeout.Infinite); return 0;
        }
        if (mode == "flood")
            for (int i=0;i<10000;i++) { Console.WriteLine("OUT "+i); Console.Error.WriteLine("ERR "+i); }
        Console.WriteLine("healthy resource 子项转码完成"); return 0;
    }
    private static void Run(string directory)
    {
        directory = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var prototype=new Node("prototype",true,true); // even a misleading storage path must not stop expansion
        var a=new Node("placed-A",false,true); var b=new Node("placed-B",false,true);
        var missing=new Node("missing"); var nested=new Node("nested",true);
        var geometry=new Node("internal"); a.Kids.Add(geometry);
        prototype.Kids.Add(a); prototype.Kids.Add(nested); nested.Kids.Add(b); nested.Kids.Add(missing); nested.Kids.Add(prototype);
        var resources=JtBatchSafety.Expand(new[]{prototype,a},n=>n.Id,n=>n.Prototype,n=>n.Backing,n=>n.Kids);
        Check(resources.Count==3 && resources.Contains(a) && resources.Contains(b) && resources.Contains(missing),"Prototype/overlapping selection/cycle expansion");
        Check(!resources.Contains(geometry) && !resources.Contains(prototype),"Exported a container or internal geometry");
        Console.WriteLine("PASS prototype recursion; overlapping picks; cycles; two backing instances kept; missing leaf retained");
        int expandErrors=0; var bad=new Node("bad-SDK-object");
        var mixed=JtBatchSafety.Expand(new[]{bad,b},n=>n.Id,n=>n.Prototype,n=>n.Backing,
            n=> { if(n==bad)throw new InvalidOperationException("fixture SDK failure"); return n.Kids; },(n,e)=>expandErrors++);
        Check(expandErrors==1 && mixed.Contains(b) && mixed.Contains(bad),"SDK error aborted healthy selection");
        Console.WriteLine("PASS per-object SDK enumeration failure isolated");

        var slow=new TaskCompletionSource<int>(); var fault=new TaskCompletionSource<int>(); var cancel=new TaskCompletionSource<int>();
        fault.SetException(new InvalidDataException("bad resource")); cancel.SetCanceled();
        var tasks=new List<Task<int>>{slow.Task,fault.Task,cancel.Task};
        Check(JtBatchSafety.TakeCompleted(tasks,t=>t)==fault.Task,"FIFO blocked a completed failure");
        Check(JtBatchSafety.TakeCompleted(tasks,t=>t)==cancel.Task,"Cancellation blocked siblings");
        try { fault.Task.GetAwaiter().GetResult(); } catch(InvalidDataException) { }
        slow.SetResult(1); Check(JtBatchSafety.TakeCompleted(tasks,t=>t).Result==1,"Healthy resource lost");
        Console.WriteLine("PASS completion-first handling of faulted/cancelled/healthy tasks");

        var jobs=new List<Task<int>>(); string hungMarker=Path.Combine(directory,"hung");
        foreach(var mode in new[]{"hang","error","flood","ok"})
        {
            string marker=mode=="hang"?hungMarker:Path.Combine(directory,mode);
            jobs.Add(Task.Run(()=>{
                try { return JtProcessRunner.Run(Exe,"fake "+mode+" "+JtProcessRunner.Quote(marker),marker+".log",mode=="hang"?1500:10000,true,()=>File.WriteAllText(marker+".go","go")); }
                catch(TimeoutException) { return -1; }
            }));
        }
        var watch=Stopwatch.StartNew(); int healthy=0, failed=0, timedout=0;
        while(jobs.Count>0)
        {
            int exit=JtBatchSafety.TakeCompleted(jobs,t=>t).GetAwaiter().GetResult();
            if(exit==0)healthy++; else if(exit==-1)timedout++; else failed++;
        }
        Check(watch.ElapsedMilliseconds<12000 && healthy==2 && failed==1 && timedout==1,"Failure/timeout did not isolate other jobs");
        Check(File.Exists(hungMarker+".pid"),"Nested process did not start");
        int pid=int.Parse(File.ReadAllText(hungMarker+".pid")); bool alive=false;
        try { using(var p=Process.GetProcessById(pid)) alive=!p.HasExited; } catch(ArgumentException) { }
        Check(!alive,"Timed-out resource left its decoder child alive");
        Check(File.ReadAllLines(Path.Combine(directory,"flood.log")).Length==20001,"stdout/stderr capture incomplete");
        Check(File.ReadAllText(Path.Combine(directory,"ok.log")).Contains("子项转码完成"),"UTF-8 worker log was corrupted");
        Console.WriteLine("PASS exit failure + hung child tree + simultaneous stdout/stderr flood; healthy siblings=2; no live descendants; ms="+watch.ElapsedMilliseconds);
        string orphan=Path.Combine(directory,"orphanpipe"); var drainWatch=Stopwatch.StartNew(); bool pipeTimeout=false;
        try { JtProcessRunner.Run(Exe,"fake orphanpipe "+JtProcessRunner.Quote(orphan),orphan+".log",10000,true,()=>File.WriteAllText(orphan+".go","go")); }
        catch(TimeoutException) {pipeTimeout=true;}
        Check(drainWatch.ElapsedMilliseconds<8000,"Exited parent waited indefinitely for inherited log pipe");
        pid=int.Parse(File.ReadAllText(orphan+".pid")); alive=false;
        try {using(var p=Process.GetProcessById(pid))alive=!p.HasExited;} catch(ArgumentException) {}
        Check(!alive,"Exited resource left a log-pipe holder alive");
        Console.WriteLine("PASS exited parent + inherited pipe holder cleanup; pipeTimeout="+pipeTimeout+"; ms="+drainWatch.ElapsedMilliseconds);
    }
}
