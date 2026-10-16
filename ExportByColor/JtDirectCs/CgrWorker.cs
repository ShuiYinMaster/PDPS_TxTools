using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using TxTools.ExportByColor;

internal static class CgrWorker
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
        AppDomain.CurrentDomain.AssemblyResolve += (s,e) => {
            string path = Path.Combine(root, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        try { return Run(args); }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Expected a detached JT job file");
        string request = Path.GetFullPath(args[0]);
        // The parent must attach us to its private Windows job before any decoder
        // child can start. A vanished parent never leaves an unowned conversion.
        var clock = Stopwatch.StartNew();
        while (!File.Exists(request + ".go"))
        {
            if (clock.ElapsedMilliseconds >= 10000) throw new TimeoutException("JT job ownership handshake timed out");
            Thread.Sleep(25);
        }
        string source, directory, decoder, output; CgrBackend backend; bool reconstruct;
        var placement = new double[16];
        using (var r = new BinaryReader(File.OpenRead(request)))
        {
            if (r.ReadString() != "JT-CGR-JOB-1") throw new InvalidDataException("Unknown job protocol");
            source = r.ReadString(); directory = r.ReadString(); decoder = r.ReadString(); output = r.ReadString();
            backend = (CgrBackend)r.ReadInt32(); reconstruct = r.ReadBoolean();
            for (int i = 0; i < placement.Length; i++) placement[i] = r.ReadDouble();
            if (r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("Trailing job data");
        }
        int inner = 0, outer = 0;
        Console.WriteLine("[JT isolated worker] " + CgrWriter.Version + "; source=" + source);
        if (reconstruct)
        {
            var holes = JtDirectBridge.ConvertToCgrWithHoles(source, directory, decoder, output, placement, backend, Console.WriteLine);
            foreach (var hole in holes) { if (hole.ExternalCylinder) outer++; else inner++; }
        }
        else JtDirectBridge.ConvertToCgr(source, directory, decoder, output, placement, backend, Console.WriteLine);
        using (var w = new BinaryWriter(File.Create(request + ".done")))
        { w.Write("JT-CGR-DONE-1"); w.Write(inner); w.Write(outer); }
        return 0;
    }
}
