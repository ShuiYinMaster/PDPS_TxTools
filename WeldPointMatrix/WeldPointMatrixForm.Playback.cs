using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Tecnomatix.Engineering;
using Tecnomatix.Engineering.Ui;
using TxTools.RobotReachabilityChecker.Services;
using TxTools.WeldSpotAllocator;

namespace TxTools.WeldPointMatrix
{
    public sealed partial class WeldPointMatrixForm
    {
        private TxSimulationPlayer _player;
        private SequenceEditorSession _sequenceSession;
        private bool _sequenceMode;
        private List<ITxObject> _playingOperations;
        private volatile bool _paused, _stopRequested;
        private int _playGeneration, _endHandled, _sdkCallDepth;
        private SynchronizationContext _playContext;
        private bool _finishRequested, _cleanupQueued;
        private string _finishMessage;

        private void InitializePlayback()
        {
            _playContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            _table.CellPainting += PaintOperationHeader;
            _table.ColumnHeaderMouseClick += HeaderPlaybackClick;
            _playbackTips = new ToolTip();
            if (_playAllButton != null)
                _playbackTips.SetToolTip(_playAllButton, "使用选中操作已有的共同复合范围，在序列编辑器中按原有时序播放；不移动操作。");
            UpdatePlaybackInterlock();
        }

        private void ToggleSelection()
        {
            if (!_selectionCollapsed) _selectionWidth = _body.ColumnStyles[0].Width;
            _selectionCollapsed = !_selectionCollapsed;
            UpdatePlaybackInterlock();
            _body.SuspendLayout();
            _selectionCard.Visible = !_selectionCollapsed;
            _body.ColumnStyles[0].Width = _selectionCollapsed ? 0 : _selectionWidth;
            _toggleSelection.Text = _selectionCollapsed ? "展开选取区" : "收起选取区";
            _body.ResumeLayout(true);
            if (_selectionCollapsed) _table.Focus();
        }

        private void AddNativeLocation(bool after)
        {
            if (!TryBeginSceneAction()) return;
            try { AddNativeLocationCore(after); }
            finally { EndSceneAction(); }
        }

        private void AddNativeLocationCore(bool after)
        {
            var cell = _table.CurrentCell;
            if (cell == null || _rows[cell.RowIndex][cell.ColumnIndex] == null)
            { SetStatus("请选中已有焊点、连续点或过渡点，再选择“前插过渡点”或“后插过渡点”。"); return; }
            try
            {
                PointService.AddNativeLocation(_operations[cell.ColumnIndex],
                    _rows[cell.RowIndex][cell.ColumnIndex], after);
                RefreshMatrix(true);
                SetStatus("已执行“" + (after ? "后插过渡点" : "前插过渡点") + "”。");
            }
            catch (Exception ex) { SetStatus("插入过渡点失败：" + ex.Message); }
        }

        private Rectangle HeaderButton(Rectangle bounds, bool pause)
        {
            int margin = Math.Max(3, (int)(4 * DeviceDpi / 96f));
            int height = Math.Max(20, bounds.Height / 2 - margin);
            int width = Math.Max(30, (bounds.Width - 3 * margin) / 2);
            return new Rectangle(bounds.Left + margin + (pause ? width + margin : 0),
                bounds.Bottom - height - margin, width, height);
        }

        private void PaintOperationHeader(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex != -1 || e.ColumnIndex < 0 || e.ColumnIndex >= _operations.Count) return;
            e.PaintBackground(e.ClipBounds, true);
            Rectangle title = e.CellBounds;
            title.Height = title.Height / 2;
            TextRenderer.DrawText(e.Graphics, _operations[e.ColumnIndex].Name, _table.Font,
                title, e.CellStyle.ForeColor, TextFormatFlags.HorizontalCenter |
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            foreach (bool pause in new[] { false, true })
            {
                Rectangle button = HeaderButton(e.CellBounds, pause);
                bool enabled = pause
                    ? !_sequenceMode && _playbackState == PlaybackState.Playing && _playingOperations != null
                        && Same(_playingOperations[0], _operations[e.ColumnIndex])
                    : CanPlayColumn(e.ColumnIndex);
                ControlPaint.DrawButton(e.Graphics, button, enabled ? ButtonState.Normal : ButtonState.Inactive);
                TextRenderer.DrawText(e.Graphics, pause ? "暂停" : "播放", _table.Font,
                    button, enabled ? SystemColors.ControlText : SystemColors.GrayText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            e.Handled = true;
        }

        private void HeaderPlaybackClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || e.ColumnIndex < 0) return;
            Rectangle bounds = _table.GetCellDisplayRectangle(e.ColumnIndex, -1, false);
            Point cursor = _table.PointToClient(Cursor.Position);
            if (HeaderButton(bounds, false).Contains(cursor)) PlayColumn(e.ColumnIndex);
            else if (HeaderButton(bounds, true).Contains(cursor)) PauseColumn(e.ColumnIndex);
        }



        private bool CanPlayColumn(int column)
        {
            if (_sceneActionBusy || _closeAfterStop || column < 0 || column >= _operations.Count) return false;
            if (_playbackState == PlaybackState.Idle) return true;
            return _playbackState == PlaybackState.Paused && _sdkCallDepth == 0
                && !_sequenceMode && !_finishRequested && _playingOperations != null
                && Same(_playingOperations[0], _operations[column]);
        }

        private void PlayColumn(int column)
        {
            if (column < 0 || column >= _operations.Count) return;
            StartTrajectory(new List<ITxObject> { _operations[column] }, false);
        }

        private void PauseColumn(int column)
        {
            if (_sequenceMode) { SetStatus("序列播放中，请使用顶部“暂停播放”。"); return; }
            if (_playingOperations == null || !_playingOperations.Any(o => Same(o, _operations[column]))) return;
            PauseAll();
        }

        private void PlayAll()
        {
            if (_operations.Count == 0) { SetStatus("请先添加要播放的操作。"); return; }
            StartTrajectory(new List<ITxObject>(_operations), true);
        }

        private void StartTrajectory(List<ITxObject> operations, bool sequenceMode)
        {
            if (operations == null || operations.Count == 0 || (!sequenceMode && operations.Count != 1)) return;
            if (_sceneActionBusy || _closeAfterStop)
            { SetStatus("请先完成当前操作后再播放。"); return; }
            bool same = _sequenceMode == sequenceMode && _playingOperations != null
                && _playingOperations.Count == operations.Count
                && operations.Select((op, i) => Same(op, _playingOperations[i])).All(match => match);
            if (PlaybackOwnsRobot)
            {
                if (_playbackState != PlaybackState.Paused || !same || _sdkCallDepth != 0 || _finishRequested)
                { SetStatus("当前播放会话尚未结束。切换操作前请先点击“停止播放”。"); return; }
                try
                {
                    _paused = false;
                    _playbackState = PlaybackState.Playing;
                    Interlocked.Exchange(ref _endHandled, 0);
                    UpdatePlaybackInterlock();
                    SetStatus("继续轨迹播放。");
                    InvokePlayerCommand(() => { if (_sequenceMode) _sequenceSession.Play(); else _player.Play(); });
                }
                catch (Exception ex) { HandlePlaybackFailure(ex); }
                return;
            }

            _playGeneration++;
            _playingOperations = operations;
            _sequenceMode = sequenceMode;
            _paused = false;
            _stopRequested = false;
            _finishRequested = false;
            _playbackState = PlaybackState.Starting;
            UpdatePlaybackInterlock();
            try
            {
                InvokePlayerCommand(() =>
                {
                    if (sequenceMode)
                    {
                        var scope = SequencePlaybackScope.Resolve(operations);
                        if (_stopRequested) return;
                        _sequenceSession = new SequenceEditorSession(scope, _playContext, () => _stopRequested, () =>
                        {
                            _playbackState = PlaybackState.Playing;
                            UpdatePlaybackInterlock();
                            SetStatus("序列播放：" + scope.Name + "（保留原有时序与绑定）");
                        });
                        if (_stopRequested) return;
                        _sequenceSession.Ended += SimulationEnded;
                        Interlocked.Exchange(ref _endHandled, 0);
                        _sequenceSession.Play();
                        return;
                    }
                    _player = new TxSimulationPlayer();
                    if (_stopRequested) return;
                    _player.SetOperation((ITxOperation)operations[0]);
                    if (_stopRequested) return;
                    _player.Rewind();
                    if (_stopRequested) return;
                    Interlocked.Exchange(ref _endHandled, 0);
                    _player.Ended += SimulationEnded;
                    _playbackState = PlaybackState.Playing;
                    UpdatePlaybackInterlock();
                    SetStatus("轨迹播放：" + operations[0].Name);
                    _player.Play();
                });
            }
            catch (Exception ex) { HandlePlaybackFailure(ex); }
        }

        private void InvokePlayerCommand(Action command)
        {
            _sdkCallDepth++;
            try { command(); }
            finally
            {
                _sdkCallDepth--;
                UpdatePlaybackInterlock();
                if (_sdkCallDepth == 0 && _finishRequested) QueuePlaybackCleanup();
            }
        }

        private void SimulationEnded(object sender, EventArgs args)
        {
            if (_paused || _stopRequested || (!ReferenceEquals(sender, _player) && !ReferenceEquals(sender, _sequenceSession))
                || Interlocked.CompareExchange(ref _endHandled, 1, 0) != 0) return;
            int generation = _playGeneration;
            _playContext.Post(_ =>
            {
                if (IsDisposed || generation != _playGeneration || (_player == null && _sequenceSession == null) || _paused || _stopRequested) return;
                _finishMessage = "轨迹播放完成，已恢复点位操作。";
                _finishRequested = true;
                QueuePlaybackCleanup();
            }, null);
        }

        private void PauseAll()
        {
            if (_playbackState != PlaybackState.Playing || (_player == null && _sequenceSession == null)) return;
            _paused = true;
            _playbackState = PlaybackState.Paused;
            UpdatePlaybackInterlock();
            try
            {
                InvokePlayerCommand(() => { if (_sequenceMode) _sequenceSession.Pause(); else _player.Pause(); });
                SetStatus("已暂停播放；定位或编辑点位前，请先点击“停止播放”。");
            }
            catch (Exception ex)
            {
                _paused = false;
                _playbackState = PlaybackState.Playing;
                UpdatePlaybackInterlock();
                SetStatus("暂停失败：" + ex.Message);
            }
        }

        private void StopPlayback()
        {
            if (_playbackState == PlaybackState.Idle || _playbackState == PlaybackState.Stopping
                || _playbackState == PlaybackState.Cleaning) return;
            _stopRequested = true;
            _playbackState = PlaybackState.Stopping;
            UpdatePlaybackInterlock();
            SetStatus("正在停止播放，请等待播放器退出。");
            try
            {
                if (_sequenceSession != null) InvokePlayerCommand(() => _sequenceSession.Stop());
                else if (_player != null) InvokePlayerCommand(() => _player.Stop());
                _finishMessage = "播放已停止，已恢复点位操作。";
                _finishRequested = true;
                QueuePlaybackCleanup();
            }
            catch (Exception ex)
            {
                _finishRequested = false;
                _playbackState = PlaybackState.Faulted;
                UpdatePlaybackInterlock();
                SetStatus("停止播放失败，点位操作仍保持锁定。请重试“停止播放”：" + ex.Message);
            }
        }

        private void HandlePlaybackFailure(Exception failure)
        {
            StopPlayback();
            if (_playbackState != PlaybackState.Faulted)
            {
                _finishMessage = "轨迹播放失败：" + failure.Message;
                SetStatus(_finishMessage + "（正在结束播放会话）");
            }
        }

        private void QueuePlaybackCleanup()
        {
            if (_cleanupQueued || !_finishRequested || _playbackState == PlaybackState.Cleaning
                || _playbackState == PlaybackState.Faulted || _sdkCallDepth != 0) return;
            _cleanupQueued = true;
            int generation = _playGeneration;
            _playContext.Post(_ =>
            {
                _cleanupQueued = false;
                if (IsDisposed || generation != _playGeneration || _sdkCallDepth != 0
                    || !_finishRequested || _playbackState == PlaybackState.Faulted) return;
                FinishPlayback();
            }, null);
        }

        private void FinishPlayback()
        {
            _playbackState = PlaybackState.Cleaning;
            UpdatePlaybackInterlock();
            try
            {
                if (_sequenceSession != null)
                {
                    bool running = true;
                    InvokePlayerCommand(() => running = _sequenceSession.IsRunning());
                    if (running) throw new InvalidOperationException("序列编辑器仍在播放，请等待后重试停止。");
                    _sequenceSession.Ended -= SimulationEnded;
                    InvokePlayerCommand(() => _sequenceSession.Dispose());
                    _sequenceSession = null;
                }
                if (_player != null)
                {
                    bool running = true;
                    InvokePlayerCommand(() => running = _player.IsSimulationRunning());
                    if (running)
                        throw new InvalidOperationException("SDK 仍报告仿真正在运行，请等待后重试停止。");
                    _player.Ended -= SimulationEnded;
                    InvokePlayerCommand(() => _player.ResetToDefaultSetting());
                }
                _player = null;
                _playingOperations = null;
                _sequenceMode = false;
                _playGeneration++;
                _paused = false;
                _stopRequested = false;
                _finishRequested = false;
                _playbackState = PlaybackState.Idle;
                UpdatePlaybackInterlock();
                SetStatus(_finishMessage);
                if (_closeAfterStop) _playContext.Post(_ =>
                {
                    _closeAfterStop = false;
                    if (!IsDisposed) Close();
                }, null);
            }
            catch (Exception ex)
            {
                _finishRequested = false;
                _playbackState = PlaybackState.Faulted;
                UpdatePlaybackInterlock();
                SetStatus("播放器清理失败，点位操作仍保持锁定。请重试“停止播放”：" + ex.Message);
            }
        }

        private void DisposePlayback()
        {
            _playbackTips?.Dispose();
        }
    }
}
