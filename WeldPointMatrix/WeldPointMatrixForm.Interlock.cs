using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Tecnomatix.Engineering.Ui;

namespace TxTools.WeldPointMatrix
{
    public sealed partial class WeldPointMatrixForm
    {
        private enum PlaybackState { Idle, Starting, Playing, Paused, Stopping, Cleaning, Faulted }
        private PlaybackState _playbackState;
        private bool _sceneActionBusy;
        private bool _closeAfterStop;
        private readonly List<Control> _editControls = new List<Control>();
        private Button _stopPlaybackButton, _pausePlaybackButton, _playAllButton;
        private ToolTip _playbackTips;

        private bool PlaybackOwnsRobot => _playbackState != PlaybackState.Idle;

        private bool CanEditScene(bool report = true)
        {
            if (!_closing && !PlaybackOwnsRobot && !_sceneActionBusy && !_closeAfterStop) return true;
            if (report) SetStatus(PlaybackOwnsRobot
                ? "播放会话尚未结束，暂不能定位或编辑点位。请先点击“停止播放”，等待停止完成。"
                : "当前点位操作尚未完成，请稍候。");
            return false;
        }

        private bool TryBeginSceneAction()
        {
            if (!CanEditScene()) return false;
            _sceneActionBusy = true;
            UpdatePlaybackInterlock();
            return true;
        }

        private void EndSceneAction()
        {
            _sceneActionBusy = false;
            UpdatePlaybackInterlock();
        }

        private void UpdatePlaybackInterlock()
        {
            bool editable = !_closing && !PlaybackOwnsRobot && !_sceneActionBusy && !_closeAfterStop;
            // Suspend before disabling controls: LoseFocus requires a live native picker.
            // Unsuspending never enables ListenToPick; the next user activation does that.
            foreach (TxObjGridCtrl grid in _pickerHost.Controls.OfType<TxObjGridCtrl>())
                TxTools.Common.PickFocus.SetSuspended(grid, !editable || _selectionCollapsed);
            foreach (Control control in _editControls) control.Enabled = editable;
            _pickerHost.Enabled = editable;
            _table.AllowDrop = editable;
            if (_playAllButton != null)
                _playAllButton.Enabled = (editable && _operations.Count > 0)
                    || (_sequenceMode && _playbackState == PlaybackState.Paused && _sdkCallDepth == 0 && !_finishRequested);
            if (_pausePlaybackButton != null)
                _pausePlaybackButton.Enabled = _playbackState == PlaybackState.Playing;
            if (_stopPlaybackButton != null)
                _stopPlaybackButton.Enabled = PlaybackOwnsRobot
                    && _playbackState != PlaybackState.Cleaning && _playbackState != PlaybackState.Stopping;
            _table.Invalidate();
        }

        private void PlaybackFormClosing(object sender, FormClosingEventArgs e)
        {
            if (_sceneActionBusy)
            {
                e.Cancel = true;
                SetStatus("请先完成当前点位操作后再关闭窗口。");
                return;
            }
            if (!PlaybackOwnsRobot) return;
            e.Cancel = true;
            _closeAfterStop = true;
            StopPlayback();
        }
    }
}
