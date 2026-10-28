using System;
using System.Threading;
using Tecnomatix.Engineering;

namespace TxTools.WeldPointMatrix
{
    internal sealed class SequenceEditorSession : IDisposable
    {
        private readonly TxDocument _document;
        private readonly TxSequenceEditorViewer _viewer;
        private readonly ITxOperation _scope, _previousOperation;
        private readonly SynchronizationContext _context;
        private readonly int _uiThread;
        private readonly Func<bool> _cancelRequested;
        private readonly Action _onStarting;
        private TxSimulationPlayer _activePlayer;
        private bool _disposed, _loaded, _subscribed, _startedByUs;

        internal event EventHandler Ended;

        internal SequenceEditorSession(ITxOperation scope, SynchronizationContext context,
            Func<bool> cancelRequested, Action onStarting)
        {
            _context = context;
            _cancelRequested = cancelRequested ?? (() => false);
            _onStarting = onStarting;
            _uiThread = Thread.CurrentThread.ManagedThreadId;
            _document = TxApplication.ActiveDocument;
            _viewer = TxApplication.ViewersManager.SequenceEditor;
            if (_viewer == null) throw new InvalidOperationException("请先在 PS 中打开序列编辑器。");
            _scope = scope;
            _previousOperation = _document.CurrentOperation;
        }

        internal void Play()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SequenceEditorSession));
            if (_cancelRequested()) return;
            if (!_loaded)
            {
                if (_document.SimulationPlayer.IsSimulationRunning())
                    throw new InvalidOperationException("PS 正在播放其他仿真，请先停止原生播放。");
                _loaded = true;
                _document.CurrentOperation = _scope;
                if (_cancelRequested()) return;
                if (!_viewer.IsOpen) TxApplication.ViewersManager.ShowViewer(_viewer.Name);
                if (_cancelRequested()) return;
                _viewer.SimulationStarted += SimulationStarted;
                _subscribed = true;
            }
            if (!_viewer.CanPlay) throw new InvalidOperationException("序列编辑器当前不能播放，请检查当前操作和序列时序。");
            BindActivePlayer();
            if (_cancelRequested()) return;
            _onStarting?.Invoke();
            if (_cancelRequested()) return;
            _startedByUs = true;
            _viewer.Play();
            // Some hosts finish synchronously. Capture the native scheduler even if
            // SimulationStarted was marshaled after the Play call returned.
            BindActivePlayer();
            if (!_activePlayer.IsSimulationRunning()) Ended?.Invoke(this, EventArgs.Empty);
        }

        internal void Pause() { if (_startedByUs) _viewer.Stop(); }
        internal void Stop() { if (_startedByUs) _viewer.Stop(); }

        internal bool IsRunning()
        {
            if (!_startedByUs) return false;
            BindActivePlayer();
            return _activePlayer.IsSimulationRunning();
        }

        private void SimulationStarted(object sender, TxSimulationViewer_SimulationStartedEventArgs args)
        {
            if (Thread.CurrentThread.ManagedThreadId == _uiThread) BindActivePlayer();
            else _context.Post(_ => { if (!_disposed) BindActivePlayer(); }, null);
        }

        private void BindActivePlayer()
        {
            if (_disposed) return;
            var active = _document.SimulationPlayer;
            if (ReferenceEquals(active, _activePlayer)) return;
            if (_activePlayer != null) _activePlayer.Ended -= PlayerEnded;
            _activePlayer = active;
            _activePlayer.Ended += PlayerEnded;
        }

        private void PlayerEnded(object sender, EventArgs args)
        {
            if (!_disposed && _startedByUs && ReferenceEquals(sender, _activePlayer)) Ended?.Invoke(this, args);
        }

        public void Dispose()
        {
            if (_disposed) return;
            if (_subscribed) { _viewer.SimulationStarted -= SimulationStarted; _subscribed = false; }
            if (_activePlayer != null) _activePlayer.Ended -= PlayerEnded;
            // Do not reset or rebuild the native sequence; restore only our current-operation choice.
            if (_loaded && Equals(_document.CurrentOperation, _scope))
                _document.CurrentOperation = _previousOperation;
            _disposed = true;
        }
    }
}
