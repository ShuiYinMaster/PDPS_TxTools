using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

internal static class PsDispatchRegression
{
    private sealed class SendOnlyContext : SynchronizationContext { public override void Send(SendOrPostCallback d, object s) { d(s); } }
    private sealed class UiContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<Action> queue = new BlockingCollection<Action>();
        private readonly Thread thread;
        internal int ThreadId { get { return thread.ManagedThreadId; } }
        internal UiContext() { thread=new Thread(()=>{foreach(var action in queue.GetConsumingEnumerable())action();}); thread.IsBackground=true; thread.Start(); }
        public override void Post(SendOrPostCallback callback, object state) { queue.Add(()=>callback(state)); }
        public void Dispose() { queue.CompleteAdding(); if(!thread.Join(1000))throw new Exception("UI fixture did not exit"); queue.Dispose(); }
    }
    private static int Invoke(object service, MethodInfo method, Func<int> action)
    {
        try { return (int)method.Invoke(service,new object[]{action}); }
        catch(TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static int Main(string[] args)
    {
        try
        {
            string bin=Path.GetFullPath(args[0]);
            AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=> {
                foreach(string dir in new[]{bin,Path.Combine(Path.GetDirectoryName(bin),"2402dll")})
                {string p=Path.Combine(dir,new AssemblyName(e.Name).Name+".dll");if(File.Exists(p))return Assembly.LoadFrom(p);} return null;
            };
            var type=Assembly.LoadFrom(Path.Combine(bin,"TxTools.dll")).GetType("TxTools.ExportByColor.ExportByColorService",true);
            var method=type.GetMethod("OnPsJt",BindingFlags.Instance|BindingFlags.NonPublic).MakeGenericMethod(typeof(int));
            using(var ui=new UiContext())
            {
                var service=Activator.CreateInstance(type,new object[]{ui});
                int actual=Task.Run(()=>Invoke(service,method,()=>Thread.CurrentThread.ManagedThreadId)).GetAwaiter().GetResult();
                if(actual!=ui.ThreadId)throw new Exception("Metadata did not run on supplied UI context");
                bool failure=false;
                try {Invoke(service,method,()=>{throw new InvalidDataException("fixture metadata failure");});} catch(InvalidDataException) {failure=true;}
                if(!failure || Invoke(service,method,()=>42)!=42)throw new Exception("Metadata exception blocked next resource");
            }
            foreach(var context in new SynchronizationContext[]{new SynchronizationContext(),new SendOnlyContext()})
            {
                bool rejected=false;var service=Activator.CreateInstance(type,new object[]{context});
                try {Invoke(service,method,()=>1);} catch(InvalidOperationException) {rejected=true;}
                if(!rejected)throw new Exception("Thread-pool Post context was accepted");
            }
            Console.WriteLine("PASS formal DLL metadata dispatch stays on supplied UI thread; exception isolated; plain/Send-only contexts rejected; no PS scene access");
            return 0;
        }
        catch(Exception ex) {Console.Error.WriteLine(ex);return 1;}
    }
}
