using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TxTools.FootingPlanExport
{
    internal static class ConverterRunner
    {
        internal static string Executable => Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "FootingPlanExport", "converter", "JtToDxf.exe");

        internal static Task RunAsync(string jt, string dxf, CancellationToken token, IProgress<int> progress)
            => RunExecutableAsync(Executable, jt, dxf, token, progress);

        internal static async Task RunExecutableAsync(string executable, string jt, string dxf, CancellationToken token, IProgress<int> progress)
        {
            if (!File.Exists(executable)) throw new FileNotFoundException("缺少 JT→DXF 转换器，请完整安装插件包。", executable);
            var start = new ProcessStartInfo(executable, Quote(jt) + " " + Quote(dxf) + " --progress")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(executable)
            };
            using (var process = new Process { StartInfo = start })
            {
                token.ThrowIfCancellationRequested();
                if (!process.Start()) throw new IOException("无法启动 JT→DXF 转换器。");
                var stderr = process.StandardError.ReadToEndAsync();
                using (token.Register(() => { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } }))
                {
                    string line;
                    while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        if (line.StartsWith("::progress::", StringComparison.Ordinal))
                        {
                            int end = line.IndexOf('|');
                            int value;
                            if (end > 12 && int.TryParse(line.Substring(12, end - 12), out value)) progress.Report(Math.Max(0, Math.Min(100, value)));
                        }
                    }
                    await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
                    var errors = await stderr.ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (process.ExitCode != 0) throw new IOException("JT→DXF 转换失败：\n" + errors);
                }
            }
            if (!File.Exists(dxf) || new FileInfo(dxf).Length == 0) throw new IOException("转换器没有生成 DXF。");
        }

        // Windows CommandLineToArgvW quoting, including trailing backslashes.
        internal static string Quote(string value)
        {
            if (value == null || value.IndexOf('\0') >= 0) throw new ArgumentException("Invalid process argument");
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') result.Append('\\', slashes * 2 + 1); else result.Append('\\', slashes);
                result.Append(c); slashes = 0;
            }
            result.Append('\\', slashes * 2).Append('"');
            return result.ToString();
        }
    }
}
