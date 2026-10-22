using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using TxTools.ExportGun;

internal static class Program
{
    internal static int UiThread;
    private static int assertions;
    internal static void Check(bool ok, string message)
    { if (!ok) throw new Exception(message); assertions++; }

    [STAThread]
    private static int Main()
    {
        try
        {
            UiThread = Thread.CurrentThread.ManagedThreadId;
            VerifyNaming();
            VerifyCgrPaths();
            foreach (var context in new SynchronizationContext[] { null, new SynchronizationContext(), new WindowsFormsSynchronizationContext() })
            {
                using (var service = new ExportService(context))
                {
                    Run(service, false, false, false);
                    Run(service, true, false, false);
                    Run(service, false, true, false);
                    Run(service, true, true, false);
                    Run(service, false, false, true);
                    Run(service, true, false, true);
                    Run(service, false, false, false, true);
                    Run(service, true, false, false, true);
                }
            }
            Console.WriteLine("PASS: " + assertions + " export threading, lifetime and failure assertions");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void VerifyNaming()
    {
        var day = new DateTime(2026, 10, 20, 23, 59, 59);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "OP-20261020-001", "op-20261020-003", "OP-20261019-999" };
        Check(GunExportNaming.NextOperationName("OP", day, used) == "OP-20261020-004", "Existing part/instance names must advance the sequence");
        Check(GunExportNaming.NextOperationName("OP", day, used) == "OP-20261020-005", "Same export must reserve its generated names");
        Check(GunExportNaming.NextOperationName("OP", day.AddDays(1), used) == "OP-20261021-001", "New day must start a new sequence");
        Check(GunExportNaming.NextOperationName("焊接/操作:01", day, used) == "焊接_操作_01-20261020-001", "Chinese operation names or illegal characters changed incorrectly");
        string longName = new string('长', 100);
        string first = GunExportNaming.NextOperationName(longName, day, used);
        Check(first.Length == 80 && first.EndsWith("-20261020-001"), "Long names must retain the date and sequence");
        Check(GunExportNaming.NextOperationName(longName, day, used).EndsWith("-002"), "Truncated names must still avoid collisions");
        used.Add("B-20261020-999");
        Check(GunExportNaming.NextOperationName("B", day, used) == "B-20261020-1000", "Sequence must support more than 999 exports");
        Check(GunExportNaming.NextOperationName(" ", day, used) == "Operation-20261020-001", "Empty operation name fallback");
    }

    private static void VerifyCgrPaths()
    {
        string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "path-check-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        string preferred = Path.Combine(directory, "jt");
        string cache = Path.Combine(directory, "cache");
        Directory.CreateDirectory(preferred);
        var roots = new[] { cache };
        string normal = CgrOutputPaths.Create(preferred, "Gun", roots);
        Check(normal == Path.Combine(preferred, "Gun.cgr"), "Short JT output should retain the original name");
        WriteCgrPathFixture(normal);
        string repeated = CgrOutputPaths.Create(preferred, "Gun", roots);
        Check(repeated != normal && File.ReadAllText(normal) == "test geometry", "Generation must not replace an existing referenced CGR");
        WriteCgrPathFixture(repeated);
        string longName = new string('焊', 400);
        string first = CgrOutputPaths.Create(preferred, longName + "A", roots);
        string second = CgrOutputPaths.Create(preferred, longName + "B", roots);
        Check(first != second, "Truncating long tool names must retain their identity");
        Check(Path.GetDirectoryName(first) == preferred, "Long file names should be shortened within the JT directory");
        WriteCgrPathFixture(first);
        WriteCgrPathFixture(second);
        string fallback = CgrOutputPaths.Create(preferred + new string('x', 300), longName, roots);
        Check(fallback.StartsWith(cache + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Deep JT directory must use the short cache");
        WriteCgrPathFixture(fallback);
        string missing = CgrOutputPaths.Create(null, "未知工具", roots);
        Check(missing.StartsWith(cache + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Loaded tools without a JT directory must still generate");
        WriteCgrPathFixture(missing);
        string reserved = CgrOutputPaths.Create(preferred, "CON", roots);
        Check(Path.GetFileName(reserved) == "_CON.cgr", "Windows reserved file name");
        WriteCgrPathFixture(reserved);
        string invalid = CgrOutputPaths.Create(preferred, "焊钳:01?. ", roots);
        WriteCgrPathFixture(invalid);
    }

    private static void WriteCgrPathFixture(string output)
    {
        string temporary = output + "." + Guid.NewGuid().ToString("N") + ".partial";
        Check(output.Length <= 200 && temporary.Length < 260, "Final/temporary path exceeds the legacy API budget");
        Check(Path.GetDirectoryName(temporary).Length < 248 && Path.GetFileName(temporary).Length <= 255, "Directory/file component exceeds Windows limits");
        File.WriteAllText(temporary, "test geometry");
        File.Move(temporary, output);
        Check(File.ReadAllText(output) == "test geometry", "Short-path temporary write/publish failed");
    }

    private static void Run(ExportService service, bool balls, bool fail, bool noPoints, bool connectionFailure = false)
    {
        CatiaBridge.ThrowOnExport = fail;
        CatiaBridge.FailConnect = connectionFailure;
        PsReader.NoPoints = noPoints;
        int created = CatiaBridge.Created;
        int disposed = CatiaBridge.Disposed;
        var log = new List<string>();
        bool result = false;
        string message = null;
        using (var done = new ManualResetEvent(false))
        {
            Action<bool, string> complete = (ok, text) => { result = ok; message = text; done.Set(); };
            var ops = new List<OperationInfo> { new OperationInfo { Name = "OP" } };
            if (balls) service.ExportBallsAsync(new BallExportParams { Operations = ops }, log.Add, null, complete);
            else service.ExportGunsAsync(new GunExportParams { Operations = ops, GunOriginAtTCP = true, TcpName = "TEST_TCP" }, log.Add, null, complete);
            DateTime limit = DateTime.UtcNow.AddSeconds(10);
            while (!done.WaitOne(10))
            {
                Application.DoEvents();
                if (DateTime.UtcNow > limit) throw new TimeoutException("UI marshal deadlock");
            }
            // The worker disposes its bridge immediately after the completion callback.
            while (CatiaBridge.Disposed < disposed + (noPoints ? 0 : 1))
            {
                if (DateTime.UtcNow > limit) throw new TimeoutException("Bridge disposal missing");
                Application.DoEvents(); Thread.Sleep(1);
            }
        }
        Check(result == (!fail && !noPoints && !connectionFailure), "False success/failure for " + (balls ? "balls" : "guns"));
        Check(CatiaBridge.Created == created + (noPoints ? 0 : 1), "Connection opened before validating points");
        Check(CatiaBridge.Disposed == disposed + (noPoints ? 0 : 1), "Connection not released");
        if (fail && !noPoints)
        {
            Check(message.Contains("80004005"), "Original HRESULT lost");
            Check(log.Exists(s => s.Contains("Simulated CATIA failure")), "Failure details not logged");
        }
        if (connectionFailure)
        {
            Check(message.Contains("No CATIA instance"), "Connection error lost");
            Check(message.Contains("800401E3"), "Connection HRESULT lost");
        }
    }
}

namespace TxTools.ExportGun
{
    public enum PointType { All }
    public class PointInfo { }
    public class GunInfo { }
    public class OperationInfo
    {
        public string Name; public object PsObject; public GunInfo Gun;
        public List<PointInfo> Points = new List<PointInfo>();
    }
    public class GunExportParams
    {
        public List<OperationInfo> Operations;
        public PointType PointFilter; public bool UseMfgName; public string CustomModelPath;
        public bool GunOriginAtTCP; public string TcpName; public double[] TcpCustomMatrix;
        internal Dictionary<string, double[]> ResolvedTcpMatrices;
    }
    public class BallExportParams : GunExportParams { }
    public class ExportProgress { }
    public static class PsReader
    {
        public static bool NoPoints;
        public static List<OperationInfo> GetOperationsFromSelection(Action<string> log) => new List<OperationInfo>();
        public static void FillPoints(OperationInfo op, PointType pt, bool useMfg, Action<string> log)
        {
            Program.Check(Thread.CurrentThread.ManagedThreadId == Program.UiThread, "PS API ran outside UI thread");
            if (!NoPoints) op.Points.Add(new PointInfo());
        }
        public static GunInfo GetGunFromOperation(OperationInfo op, string path, Action<string> log)
        {
            Program.Check(Thread.CurrentThread.ManagedThreadId == Program.UiThread, "Gun read outside UI thread");
            return new GunInfo();
        }
        public static double[] ResolveTcpWorldByName(OperationInfo op, string name, Action<string> log)
        {
            Program.Check(Thread.CurrentThread.ManagedThreadId == Program.UiThread, "Named TCP read outside UI thread");
            return new double[16];
        }
    }
    public sealed class CatiaBridge : IDisposable
    {
        public static bool ThrowOnExport, FailConnect;
        public static int Created, Disposed;
        internal Exception LastConnectionError { get; private set; }
        private readonly int owner = Thread.CurrentThread.ManagedThreadId;
        public CatiaBridge() { Verify(); Interlocked.Increment(ref Created); }
        private void Verify()
        {
            Program.Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "CATIA requires STA worker");
            Program.Check(Thread.CurrentThread.ManagedThreadId == owner && owner != Program.UiThread, "CATIA connection crossed threads");
        }
        public bool Connect(out string error)
        {
            Verify(); error = FailConnect ? "No CATIA instance" : null;
            LastConnectionError = FailConnect ? new COMException(error, unchecked((int)0x800401E3)) : null;
            return !FailConnect;
        }
        public void ExportGuns(GunExportParams p, Action<ExportProgress> progress, Action<string> log)
        {
            Program.Check(p.ResolvedTcpMatrices != null && p.ResolvedTcpMatrices.ContainsKey("OP"), "Named TCP not captured before CATIA worker");
            Export();
        }
        public void ExportBalls(BallExportParams p, Action<ExportProgress> progress, Action<string> log) { Export(); }
        private void Export()
        {
            Verify();
            if (ThrowOnExport) throw new InvalidOperationException("Simulated CATIA failure", new COMException("E_FAIL", unchecked((int)0x80004005)));
        }
        public void Dispose() { Verify(); Interlocked.Increment(ref Disposed); }
    }
    public static class ExcelExporter
    {
        public static string Export(List<OperationInfo> ops, double[] matrix, string folder, Action<string> log) => "unused";
    }
}
