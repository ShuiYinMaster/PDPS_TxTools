using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace TxTools.CatiaPartTree.Core
{
    // Native tessellation is captured before starting the disposable converter
    // process. Cancelling that process must never interrupt CATIA setting restore.
    internal sealed class CatiaTessellation : IDisposable
    {
        internal string File, Hash;
        private string _directory;
        internal interface IBackend : IDisposable
        {
            object Get(string name);
            void Set(string name, object value);
            string Open(string path);
            void Export(string path);
            void Close();
        }

        internal static CatiaTessellation Capture(string source, string hash, CancellationToken cancel)
        {
            CatiaTessellation result = null; Exception error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    using (var mutex = new Mutex(false, @"Local\TxTools.CatiaTessellation"))
                    {
                        bool owned = false;
                        try
                        {
                            try { owned = WaitHandle.WaitAny(new WaitHandle[] { mutex, cancel.WaitHandle }) == 0; }
                            catch (AbandonedMutexException) { owned = true; }
                            cancel.ThrowIfCancellationRequested();
                            using (var backend = new ComBackend()) result = Capture(source, hash, backend, cancel);
                        }
                        finally { if (owned) mutex.ReleaseMutex(); }
                    }
                }
                catch (Exception ex) { error = ex is AggregateException ? ex : ex.GetBaseException(); }
            });
            thread.IsBackground = true; thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
            if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
            return result;
        }

        internal static CatiaTessellation Capture(string source, string hash, IBackend backend, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            var result = new CatiaTessellation { _directory = Path.Combine(Path.GetTempPath(), "TxTools-CAD-" + Guid.NewGuid().ToString("N")) };
            Directory.CreateDirectory(result._directory);
            string copy = Path.Combine(result._directory, "source-" + Guid.NewGuid().ToString("N") + ".CATPart");
            string[] settings = { "Viz3DFixedAccuracy", "Viz3DProportionnalAccuracy", "Viz3DCurveAccuracy" };
            object[] previous = new object[settings.Length]; int captured = 0; bool opened = false, complete = false;
            var restoreErrors = new List<Exception>();
            try
            {
                System.IO.File.Copy(source, copy);
                System.IO.File.SetAttributes(copy, FileAttributes.Normal);
                if (CatiaImportSource.Hash(copy) != hash) throw new IOException("CATPart 在曲面细分前发生变化，请重新读取来源。");
                for (; captured < settings.Length; captured++) previous[captured] = backend.Get(settings[captured]);
                // Both accuracy modes use the same sag; the user's mode is untouched.
                backend.Set(settings[0], 0.01); backend.Set(settings[1], 0.01); backend.Set(settings[2], 0.1);
                opened = true;
                string actual = backend.Open(copy);
                // CATIA can return an already loaded same-name file from another
                // directory. A unique copy plus FullName keeps geometry identity exact.
                if (!string.Equals(Path.GetFullPath(actual), Path.GetFullPath(copy), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("CATIA 打开了不同路径的文档，已保留原文档并停止此次曲面细分。");
                result.File = Path.Combine(result._directory, "surface.cgr");
                backend.Export(result.File);
                cancel.ThrowIfCancellationRequested();
                result.Hash = CatiaImportSource.Hash(result.File);
                if (CatiaImportSource.Hash(source) != hash) throw new IOException("CATPart 在曲面细分期间发生变化，请重新读取来源。");
                complete = true;
            }
            finally
            {
                // Always attempt every restore, including after a partial setting
                // change, failed Open/Export, or a cancellation request.
                if (opened) try { backend.Close(); } catch (Exception ex) { restoreErrors.Add(ex); }
                for (int i = captured - 1; i >= 0; i--)
                    try { backend.Set(settings[i], previous[i]); } catch (Exception ex) { restoreErrors.Add(ex); }
                try { if (System.IO.File.Exists(copy)) System.IO.File.Delete(copy); }
                finally { if (!complete || restoreErrors.Count != 0) result.Dispose(); }
                if (restoreErrors.Count != 0) throw new AggregateException("CATIA 文档或显示精度未能恢复，请检查 CATIA 状态。", restoreErrors);
            }
            return result;
        }

        public void Dispose()
        {
            if (File != null && System.IO.File.Exists(File)) System.IO.File.Delete(File);
            if (_directory != null && Directory.Exists(_directory) && !Directory.EnumerateFileSystemEntries(_directory).Any()) Directory.Delete(_directory);
        }

        private sealed class ComBackend : IBackend
        {
            private readonly List<object> _objects = new List<object>();
            private object _app, _settings, _documents, _document, _active;
            private string _openedPath;
            private object Track(object value)
            { if (value != null && Marshal.IsComObject(value) && !_objects.Any(o => ReferenceEquals(o, value))) _objects.Add(value); return value; }
            private object Property(object value, string name)
            { return Track(value.GetType().InvokeMember(name, BindingFlags.GetProperty, null, value, null)); }
            private object Call(object value, string name, params object[] args)
            { return Track(value.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, value, args)); }
            internal ComBackend()
            {
                try
                {
                    _app = Track(Marshal.GetActiveObject("CATIA.Application"));
                    _documents = Property(_app, "Documents");
                    _settings = Call(Property(_app, "SettingControllers"), "Item", "CATVizVisualizationSettingCtrl");
                    try { _active = Property(_app, "ActiveDocument"); }
                    catch (Exception ex) when (ex.GetBaseException() is COMException) { }
                }
                catch { Dispose(); throw; }
            }
            public object Get(string name) { return Property(_settings, name); }
            public void Set(string name, object value)
            { _settings.GetType().InvokeMember(name, BindingFlags.SetProperty, null, _settings, new[] { value }); }
            public string Open(string path)
            { _openedPath = path; _document = Call(_documents, "Open", path); return Convert.ToString(Property(_document, "FullName"), CultureInfo.InvariantCulture); }
            public void Export(string path) { Call(_document, "ExportData", path, "cgr"); }
            public void Close()
            {
                if (_document == null) return;
                string actual = Convert.ToString(Property(_document, "FullName"), CultureInfo.InvariantCulture);
                if (string.Equals(actual, _openedPath, StringComparison.OrdinalIgnoreCase)) Call(_document, "Close");
                _document = null;
            }
            public void Dispose()
            {
                if (_active != null) try { Call(_active, "Activate"); } catch (Exception) { }
                for (int i = _objects.Count - 1; i >= 0; i--) try { Marshal.ReleaseComObject(_objects[i]); } catch (InvalidComObjectException) { }
                _objects.Clear();
            }
        }
    }
}
