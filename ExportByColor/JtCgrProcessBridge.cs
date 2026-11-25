using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace TxTools.ExportByColor
{
    internal static class JtCgrProcessBridge
    {
        private const string DecoderName = "TxTools.JtDecoder.exe";
        private const string WorkerName = "TxTools.JtCgrWorker.exe";

        internal static string ResolveDecoder(Assembly assembly)
        {
            Uri original;
            string originalPath = Uri.TryCreate(assembly.CodeBase, UriKind.Absolute, out original) && original.IsFile
                ? original.LocalPath : null;
            string requirement = null;
            foreach (string path in new[] { originalPath, assembly.Location })
            {
                if (string.IsNullOrEmpty(path)) continue;
                string runtime = Path.Combine(Path.GetDirectoryName(path), "JtDirectCs");
                string decoder = Path.Combine(runtime, DecoderName);
                string worker = Path.Combine(runtime, WorkerName);
                bool hasDecoder = File.Exists(decoder);
                bool hasWorker = File.Exists(worker);
                if (hasDecoder && hasWorker) return decoder;
                if (requirement == null)
                    requirement = !hasDecoder && !hasWorker ? runtime + "（缺少两个工作进程）" : hasDecoder ? worker : decoder;
            }
            throw new FileNotFoundException("JT 转换组件缺失。请使用同一构建的 TxTools.dll 和 JtDirectCs 目录。缺项：" + (requirement ?? "找不到插件部署目录"));
        }

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
            return ConvertCancelable(source, directory, decoder, output, placement, backend, reconstruct, log, timeoutMs, default);
        }

        internal static int[] ConvertCancelable(string source, string directory, string decoder, string output,
            double[] placement, CgrBackend backend, bool reconstruct, Action<string> log, int timeoutMs,
            CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
            string request = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".jtjob");
            string worker = Path.Combine(Path.GetDirectoryName(decoder), WorkerName);
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
                    timeoutMs > 0 ? timeoutMs : TimeoutMs, true, () => File.WriteAllText(request + ".go", "go"), cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (exit == 2)
                    throw new JtDirectBridge.SourceCompatibilityException(JtProcessRunner.DescribeFailure("JT 源文件无法直读", exit, logPath));
                if (exit != 0)
                    throw new InvalidDataException(JtProcessRunner.DescribeFailure("JT→CGR 工作进程失败", exit, logPath));
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
                foreach (string suffix in new[] { ".holes.csv", ".cylinders.csv", ".cylinders.log" })
                    if (File.Exists(stagedOutput + suffix)) File.Move(stagedOutput + suffix, output + suffix);
                File.Move(stagedOutput, output);
                return counts;
            }
            finally
            {
                if (cancellation.IsCancellationRequested)
                {
                    foreach (string suffix in new[] { "", ".holes.csv", ".cylinders.csv", ".cylinders.log" })
                        try { File.Delete(stagedOutput + suffix); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
                try
                {
                    if (!cancellation.IsCancellationRequested && log != null && File.Exists(logPath))
                        foreach (string line in File.ReadLines(logPath)) { try { log(line); } catch { } }
                }
                catch { /* A diagnostic read must not replace the actual conversion failure. */ }
            }
        }
    }
}
