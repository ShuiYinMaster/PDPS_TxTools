using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Tecnomatix.Engineering;
using Tecnomatix.Engineering.Ui;
using TxTools.Common;

namespace TxTools.FootingPlanExport
{
    internal sealed class FootingPlanExportForm : TxForm
    {
        private static readonly Size DesignSize = new Size(420, 178);
        // 默认 +3 mm：地脚图通常取在设备底板略上方，0 常常落在空处。
        private const decimal DefaultHeight = 3m;
        private readonly NumericUpDown _height;
        private readonly Button _export;
        private readonly ProgressBar _progress;
        private bool _dpiApplied, _busy, _ready, _converting;
        private CacheSession _cache;
        private CancellationTokenSource _cancellation;
        private string _lastDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        internal FootingPlanExportForm()
        {
            SemiModal = false;
            FormUiKit.InitStandardForm(this, "导出地脚图", DesignSize, new Size(400, 178));
            MaximizeBox = false;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 2, RowCount = 3 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            var label = new Label { Text = "Z 距离（mm，正值向上，负值向下）", Dock = DockStyle.Fill };
            layout.Controls.Add(label, 0, 0); layout.SetColumnSpan(label, 2);
            _height = new NumericUpDown { Minimum = -1000000, Maximum = 1000000, DecimalPlaces = 3, Increment = 1, Dock = DockStyle.Fill, Value = DefaultHeight };
            _export = new Button { Text = "导出", Dock = DockStyle.Fill, Margin = new Padding(10, 0, 0, 6) };
            layout.Controls.Add(_height, 0, 1); layout.Controls.Add(_export, 1, 1);
            _progress = new ProgressBar { Minimum = 0, Maximum = 100, Dock = DockStyle.Fill, Margin = new Padding(0, 5, 0, 0) };
            layout.Controls.Add(_progress, 0, 2); layout.SetColumnSpan(_progress, 2);
            Controls.Add(layout);
            _export.Click += async (s, e) => await ExportAsync();
            FormClosing += (s, e) =>
            {
                if (_busy) { e.Cancel = true; _cancellation?.Cancel(); Text = "导出地脚图 — 正在结束当前操作"; }
            };
            FormClosed += (s, e) => _cancellation?.Dispose();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            FormUiKit.ApplyDpiScaling(this, ref _dpiApplied, DesignSize);
        }

        private void UpdateProgress(int value, string stage)
        {
            _progress.Value = Math.Max(0, Math.Min(100, value));
            Text = "导出地脚图 — " + stage;
            _progress.Refresh();
        }

        private async Task ExportAsync()
        {
            if (_busy) return;
            _busy = true; _export.Enabled = false; _height.Enabled = false;
            try
            {
                if (!_ready)
                {
                    _cache = null;
                    if (!File.Exists(ConverterRunner.Executable)) throw new FileNotFoundException("缺少转换器，请完整更新插件运行目录。", ConverterRunner.Executable);
                    var document = TxApplication.ActiveDocument;
                    if (document == null) throw new InvalidOperationException("请先打开 PS 工程。");
                    _cancellation?.Dispose(); _cancellation = new CancellationTokenSource();
                    var token = _cancellation.Token;
                    _cache = CacheSession.CreateTemporary();
                    UpdateProgress(2, "准备截取场景");
                    await Task.Delay(50); // Paint first; all SDK calls remain on PS UI thread.
                    token.ThrowIfCancellationRequested();
                    string jt = PsSectionService.Export(document, (double)_height.Value, _cache, token, UpdateProgress);
                    _converting = true;
                    try
                    {
                        var progress = new Progress<int>(value => { if (_converting) UpdateProgress(38 + value * 52 / 100, "转换 DXF " + value + "%"); });
                        await ConverterRunner.RunAsync(jt, _cache.DxfPath, token, progress);
                        token.ThrowIfCancellationRequested();
                    }
                    finally { _converting = false; }
                    _ready = true;
                }
                UpdateProgress(90, "转换完成，请选择 DXF 保存位置");
                // A single export action leads directly to the standard save dialog.
                // Cancelling it retains the ready DXF; the same button retries saving.
                // 导出名称保持纯 ASCII，避免下游 CAD/PS 出现编码退化。
                using (var dialog = new SaveFileDialog
                {
                    Title = "保存地脚图 DXF", Filter = "AutoCAD DXF (*.dxf)|*.dxf", DefaultExt = "dxf",
                    AddExtension = true, OverwritePrompt = true, InitialDirectory = _lastDirectory,
                    FileName = "FootingPlan_Z" + _height.Value.ToString("+0.###;-0.###;0", CultureInfo.InvariantCulture) + ".dxf"
                })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) { Text = "导出地脚图 — 已保留结果，点击导出重新保存"; return; }
                    UpdateProgress(94, "保存并校验 DXF");
                    await Task.Run(() => _cache.SaveDxf(dialog.FileName));
                }
                _ready = false;
                _lastDirectory = Path.GetDirectoryName(_cache.SavedPath);
                string warning = null;
                try
                {
                    UpdateProgress(98, "清理 COJT 缓存");
                    await Task.Run(() => _cache.CleanupAfterSave());
                }
                catch (Exception error) { warning = "\n缓存未能清理：" + _cache.RunDirectory + "\n" + error.Message; WriteError(error); }
                UpdateProgress(100, "导出完成");
                MessageBox.Show(this, "DXF 已保存：\n" + _cache.SavedPath + warning, "导出地脚图", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (OperationCanceledException) { Text = "导出地脚图 — 已取消，缓存保留"; }
            catch (Exception error)
            {
                WriteError(error);
                Text = "导出地脚图 — 导出失败";
                MessageBox.Show(this, error.Message + (_cache == null ? "" : "\n缓存保留：" + _cache.RunDirectory), "导出地脚图", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { _busy = false; _export.Enabled = true; _height.Enabled = !_ready; }
        }

        private void WriteError(Exception error)
        {
            if (_cache == null || !Directory.Exists(_cache.RunDirectory)) return;
            try { File.WriteAllText(Path.Combine(_cache.RunDirectory, "error.log"), error.ToString()); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
