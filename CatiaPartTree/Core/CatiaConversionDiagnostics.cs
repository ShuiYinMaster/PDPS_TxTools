using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace TxTools.CatiaPartTree.Core
{
    internal sealed class CatiaConversionDiagnostics : IDisposable
    {
        private readonly object _gate = new object();
        private readonly Action<string> _progress;
        private readonly string _part;
        private readonly Stopwatch _stepTime = Stopwatch.StartNew();
        private StreamWriter _writer;
        private string _step = "开始格式转换";
        private long _lastHeartbeat;
        internal string LogPath { get; private set; }
        internal CatiaConversionDiagnostics(string source, Action<string> progress)
        {
            _part = Path.GetFileName(source); _progress = progress;
            try
            {
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TxTools", "Logs", "CatiaImport");
                Directory.CreateDirectory(directory);
                string stem = Path.GetFileNameWithoutExtension(source);
                if (stem.Length > 64) stem = stem.Substring(0, 64);
                foreach (char c in Path.GetInvalidFileNameChars()) stem = stem.Replace(c, '_');
                string path = Path.Combine(directory, DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + stem + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".log");
                _writer = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true }; LogPath = path;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            Write("SOURCE " + source);
        }
        internal void Write(string message)
        {
            lock (_gate)
            {
                try { _writer?.WriteLine(DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture) + " " + message); }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException) { }
            }
        }
        internal void Begin(string step)
        {
            lock (_gate) { _step = step; _stepTime.Restart(); _lastHeartbeat = 0; Write("BEGIN " + step); }
            Pulse(false);
        }
        internal void Complete(string step)
        { Write("END " + step); }
        internal void Failure(Exception error)
        {
            lock (_gate) Write("ERROR " + _step + " HRESULT=0x" + error.GetBaseException().HResult.ToString("X8", CultureInfo.InvariantCulture) + " " + error);
        }
        internal T Run<T>(string step, Func<T> work)
        {
            Begin(step); var watch = Stopwatch.StartNew();
            try { var result = work(); Complete(step + " elapsed=" + watch.Elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture) + "s"); return result; }
            catch (Exception error) { Failure(error); throw; }
        }
        internal void Pulse(bool cancelled)
        {
            string message;
            lock (_gate)
            {
                long seconds = (long)_stepTime.Elapsed.TotalSeconds;
                message = _part + " · " + _step + "（" + seconds + " 秒）";
                if (cancelled) message += Environment.NewLine + "已请求取消，正在停止转换进程。";
                else if (seconds >= 15) message += Environment.NewLine + "转换进程仍在运行；曲面优化耗时取决于网格规模，可关闭窗体请求取消。";
                if (LogPath != null) message += Environment.NewLine + "日志：" + LogPath;
                if (seconds >= _lastHeartbeat + 10) { Write("WAIT " + _step + " elapsed=" + seconds + "s cancel=" + cancelled); _lastHeartbeat = seconds; }
            }
            try { _progress?.Invoke(message); } catch (Exception) { }
        }
        public void Dispose()
        {
            lock (_gate)
            {
                Write("TRACE CLOSED");
                try { _writer?.Dispose(); } catch (IOException) { }
                _writer = null;
            }
        }
    }

}
