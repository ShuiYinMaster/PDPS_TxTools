using System;
using System.IO;
using System.Text;

namespace TxTools.ExportByColor
{
    internal static class JtCgrProcessBridge
    {
        internal static int TimeoutMs
        {
            get
            {
                int seconds;
                if (!int.TryParse(Environment.GetEnvironmentVariable("TXTOOLS_JT_TIMEOUT_SECONDS"), out seconds)) seconds = 900;
                return Math.Max(30, Math.Min(3600, seconds)) * 1000;
            }
        }

        internal static int[] Convert(string source, string directory, string decoder, string output,
            double[] placement, CgrBackend backend, bool reconstruct, Action<string> log, int timeoutMs = 0)
        {
            Directory.CreateDirectory(directory);
            string request = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".jtjob");
            string worker = Path.Combine(Path.GetDirectoryName(decoder), "TxTools.JtCgrWorker.exe");
            string stagedOutput = output + ".pending.cgr";
            using (var w = new BinaryWriter(File.Create(request), new UTF8Encoding(false)))
            {
                w.Write("JT-CGR-JOB-1"); w.Write(source); w.Write(directory); w.Write(decoder); w.Write(stagedOutput);
                w.Write((int)backend); w.Write(reconstruct);
                if (placement == null || placement.Length != 16) throw new ArgumentException("Invalid resource placement");
                foreach (double v in placement) w.Write(v);
            }
            string logPath = request + ".log";
            try
            {
                int exit = JtProcessRunner.Run(worker, JtProcessRunner.Quote(request), logPath,
                    timeoutMs > 0 ? timeoutMs : TimeoutMs, true, () => File.WriteAllText(request + ".go", "go"));
                if (exit != 0) throw new InvalidDataException("JT→CGR 工作进程失败，退出码=" + exit + "；日志=" + logPath);
                int[] counts;
                using (var r = new BinaryReader(File.OpenRead(request + ".done")))
                {
                    if (r.ReadString() != "JT-CGR-DONE-1") throw new InvalidDataException("Invalid conversion receipt");
                    counts = new[] { r.ReadInt32(), r.ReadInt32() };
                    if (counts[0] < 0 || counts[1] < 0) throw new InvalidDataException("Invalid cylinder counts");
                }
                if (!File.Exists(stagedOutput)) throw new FileNotFoundException("JT worker produced no CGR", stagedOutput);
                // Publication happens only after a clean exit and receipt; never import
                // partially written files from a crashed or timed-out conversion.
                File.Move(stagedOutput, output);
                foreach (string suffix in new[] { ".holes.csv", ".cylinders.csv", ".cylinders.log" })
                    if (File.Exists(stagedOutput + suffix)) File.Move(stagedOutput + suffix, output + suffix);
                return counts;
            }
            finally
            {
                try
                {
                    if (log != null && File.Exists(logPath))
                        foreach (string line in File.ReadLines(logPath)) { try { log(line); } catch { } }
                }
                catch { /* A diagnostic read must not replace the actual conversion failure. */ }
            }
        }
    }
}
