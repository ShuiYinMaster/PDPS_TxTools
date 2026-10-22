using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TxTools.ExportByColor
{
    // Owns only the process it launches. A job owns its decoder descendants too;
    // no global process-name kill and no parameterless WaitForExit are used.
    internal static class JtProcessRunner
    {
        internal static string Quote(string value)
        {
            var b = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                b.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); b.Append(c); slashes = 0;
            }
            b.Append('\\', slashes * 2); return b.Append('"').ToString();
        }

        internal static int Run(string executable, string arguments, string logPath, int timeoutMs,
            bool ownTree = false, Action afterAssigned = null)
        {
            if (timeoutMs <= 0) throw new ArgumentOutOfRangeException("timeoutMs");
            if (!Path.IsPathRooted(executable) || !File.Exists(executable))
                throw new FileNotFoundException("JT worker is not deployed", executable);
            var clock = Stopwatch.StartNew();
            using (var process = new Process())
            using (var stdoutDone = new ManualResetEvent(false))
            using (var stderrDone = new ManualResetEvent(false))
            using (var writer = new StreamWriter(logPath, false, new UTF8Encoding(false)))
            {
                object gate = new object(); bool closed = false; Exception logError = null;
                Action<string, ManualResetEvent, string> receive = (line, eof, prefix) =>
                {
                    lock (gate)
                    {
                        if (closed) return;
                        if (line == null) { eof.Set(); return; }
                        try { writer.WriteLine(prefix + line); }
                        catch (Exception ex) { if (logError == null) logError = ex; }
                    }
                };
                process.StartInfo = new ProcessStartInfo(executable, arguments) {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                    RedirectStandardError = true, StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false), WorkingDirectory = Path.GetDirectoryName(executable)
                };
                process.OutputDataReceived += (s,e) => receive(e.Data, stdoutDone, "");
                process.ErrorDataReceived += (s,e) => receive(e.Data, stderrDone, "ERROR ");
                OwnedJob job = null; bool started = false; bool finished = false;
                try
                {
                    if (ownTree) job = new OwnedJob();
                    started = process.Start();
                    if (!started) throw new InvalidOperationException("JT worker did not start");
                    if (job != null) job.Assign(process);
                    process.BeginOutputReadLine(); process.BeginErrorReadLine();
                    if (afterAssigned != null) afterAssigned();
                    int remaining = Math.Max(1, timeoutMs - (int)Math.Min(int.MaxValue, clock.ElapsedMilliseconds));
                    if (!process.WaitForExit(remaining))
                        throw new TimeoutException("JT worker timeout; log=" + logPath);
                    // A child that inherited a pipe cannot force an unlimited EOF wait.
                    var drain = Stopwatch.StartNew();
                    if (!stdoutDone.WaitOne(5000) || !stderrDone.WaitOne(Math.Max(0, 5000 - (int)drain.ElapsedMilliseconds)))
                        throw new TimeoutException("JT worker log pipe did not close; log=" + logPath);
                    lock (gate) if (logError != null) throw new IOException("JT worker log write failed", logError);
                    finished = true;
                    return process.ExitCode;
                }
                finally
                {
                    if (job != null) job.Dispose(); // kills only this resource's remaining descendants
                    if (started && !finished)
                    {
                        try { if (!process.HasExited) process.Kill(); } catch { }
                        try { process.WaitForExit(5000); } catch { }
                    }
                    lock (gate) closed = true;
                    if (started)
                    {
                        try { process.CancelOutputRead(); } catch { }
                        try { process.CancelErrorRead(); } catch { }
                    }
                }
            }
        }

        private sealed class OwnedJob : IDisposable
        {
            private IntPtr handle;
            [StructLayout(LayoutKind.Sequential)] private struct BasicLimits {
                internal long ProcessTime, JobTime; internal uint Flags; internal UIntPtr MinSet, MaxSet;
                internal uint ActiveProcesses; internal UIntPtr Affinity; internal uint Priority, Scheduling;
            }
            [StructLayout(LayoutKind.Sequential)] private struct IoCounters {
                internal ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
            }
            [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits {
                internal BasicLimits Basic; internal IoCounters Io;
                internal UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
            }
            [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
            [DllImport("kernel32.dll", SetLastError=true)] private static extern bool SetInformationJobObject(IntPtr job, int info, ref ExtendedLimits limits, uint length);
            [DllImport("kernel32.dll", SetLastError=true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
            [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
            internal OwnedJob()
            {
                handle = CreateJobObject(IntPtr.Zero, null);
                if (handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                var limits = new ExtendedLimits(); limits.Basic.Flags = 0x2000; // KILL_ON_JOB_CLOSE
                if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf(limits)))
                {
                    int error = Marshal.GetLastWin32Error(); Dispose(); throw new System.ComponentModel.Win32Exception(error);
                }
            }
            internal void Assign(Process process)
            {
                if (!AssignProcessToJobObject(handle, process.Handle))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot isolate JT resource process");
            }
            public void Dispose() { if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; } }
        }
    }
}
