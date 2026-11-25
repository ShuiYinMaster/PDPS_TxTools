using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Registry = Microsoft.Win32.Registry;
using RegistryValueKind = Microsoft.Win32.RegistryValueKind;
using Tecnomatix.Engineering;
using Tecnomatix.Engineering.Ui;
using TxTools.Common;
using Theme = TxTools.Common.FormUiKit.Theme;
using TextBox = System.Windows.Forms.TextBox;
using Button = System.Windows.Forms.Button;

namespace TxTools.ExportByColor
{
    public class ExportByColorForm : PickAwareTxForm
    {
        internal static ExportByColorForm Instance;

        private readonly SynchronizationContext _psCtx;
        private ExportByColorService _svc;

        private TxObjGridCtrl _objGrid;
        private TxFrameEditBoxCtrl _frameOrigin;
        private CheckBox _chkAllVisible;
        private TabControl _exportTabs;
        private ExportMode _cgrMode;
        private ExportMode _meshFormat;
        private CheckBox _reconstructCylinders;
        private CheckBox _mergeMeshes;
        private RichTextBox _rtbLog;
        private Button _btnRun;
        private Button _btnEndModeling;
        private ProgressBar _progress;
        private volatile bool _closing;
        private CancellationTokenSource _exportCancellation;
        private int _lastProgressBucket;
        private string _lastLogMessage;
        private const int MaxLogCharacters = 60000;
        private const string PreferencesKey = @"Software\TxTools\ExportByColor";

        private sealed class ExportMode
        {
            internal readonly string Id, Label, Format;
            internal readonly CgrBackend Backend;
            internal bool SupportsCylinders => Format == "CGR" || Format == "JT_CGR";
            internal ExportMode(string id, string label, string format, CgrBackend backend = CgrBackend.Compact)
            { Id = id; Label = label; Format = format; Backend = backend; }
        }

        private bool _scaled;
        private readonly Size _designSize = new Size(740, 535);
        private readonly Size _minSize = new Size(650, 480);

        public ExportByColorForm(SynchronizationContext psCtx)
        {
            _psCtx = psCtx;
            SemiModal = false;
            _svc = new ExportByColorService(psCtx);
            FormUiKit.InitStandardForm(this,
                "导出 3DXML / CGR / 通用网格",
                _designSize, _minSize);
            Controls.Add(BuildBody());
        }

        public override void OnInitTxForm()
        {
            base.OnInitTxForm();
            SemiModal = false;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            FormUiKit.ApplyDpiScaling(this, ref _scaled, _designSize, scale =>
            {
                int gridHeight = (int)Math.Ceiling(32 * scale + 5 * Math.Max(27 * scale, _objGrid.Font.Height + 10 * scale));
                _objGrid.Parent.MinimumSize = new Size(0, gridHeight + _objGrid.Parent.Padding.Vertical + 2);
            });
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (e.Cancel) return;
            StopPicking();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) StopPicking();
            base.Dispose(disposing);
        }

        private void StopPicking()
        {
            if (_closing) return;
            _closing = true;
            _exportCancellation?.Cancel();
            SuspendPicking(true);
        }

        private void SuspendPicking(bool suspended)
        {
            PickFocus.SetSuspended(_objGrid, suspended);
            PickFocus.SetSuspended(_frameOrigin, suspended);
        }

        // ── 主体：资源与参数、操作行、日志和进度条 ────────────────────────
        private Control BuildBody()
        {
            var body = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4
            };
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, PluginUiPolicy.ButtonHeight + 8));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));

            var top = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1
            };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
            top.Controls.Add(BuildResourceCard(), 0, 0);
            top.Controls.Add(BuildParamCard(), 1, 0);

            body.Controls.Add(top, 0, 0);
            body.Controls.Add(BuildActionRow(), 0, 1);

            _rtbLog = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Theme.LogBg,
                ForeColor = Theme.LogText,
                Font = new Font("Consolas", 9F),
                WordWrap = false,
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(6, 2, 6, 4)
            };
            body.Controls.Add(_rtbLog, 0, 2);
            PluginLog.Register(_rtbLog);

            _progress = new ProgressBar { Dock = DockStyle.Fill, Margin = new Padding(6, 2, 6, 4) };
            body.Controls.Add(_progress, 0, 3);

            return body;
        }

        private Control BuildResourceCard()
        {
            FlowLayoutPanel content;
            var card = FormUiKit.MkCard("资源（在 PS 中拾取）", 280, 280, out content);
            card.Dock = DockStyle.Fill;
            content.AutoScroll = false;

            _objGrid = new TxObjGridCtrl
            {
                Dock = DockStyle.Fill,
                Font = FormUiKit.BaseFont,
                ListenToPick = true,
                EnableMultipleSelection = true,
                EnableRecurringObjects = false
            };
            // TxObjGridCtrl 拾取焦点统一管理：启动抢焦点 + 点击重获焦点 + ESC 取消焦点
            FormUiKit.GridPickFocus.Wire(_objGrid);

            var gridPanel = new Panel
            {
                Dock = DockStyle.Fill,
                MinimumSize = new Size(0, 171),
                BackColor = SystemColors.Window,
                Padding = new Padding(1),
                Margin = new Padding(0, 2, 0, 4),
                BorderStyle = BorderStyle.FixedSingle
            };
            gridPanel.Controls.Add(_objGrid);

            _chkAllVisible = new CheckBox
            {
                Text = "导出所有可见资源",
                AutoSize = true,
                Font = FormUiKit.BaseFont,
                Checked = false
            };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
                Padding = new Padding(8, 6, 8, 6), Margin = Padding.Empty };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(gridPanel, 0, 0);
            layout.Controls.Add(_chkAllVisible, 0, 1);
            card.Controls.Remove(content);
            card.Controls.Add(layout);
            card.Controls.SetChildIndex(layout, 0);
            content.Dispose();

            return card;
        }

        private Control BuildParamCard()
        {
            FlowLayoutPanel content;
            var card = FormUiKit.MkCard("参数", 320, 280, out content);
            card.Dock = DockStyle.Fill;
            content.AutoScroll = false;

            var rowOrigin = FormUiKit.MkRowFlow();
            rowOrigin.Controls.Add(FormUiKit.MkFieldLabel("参考坐标:"));
            _frameOrigin = new TxFrameEditBoxCtrl
            {
                Width = 190,
                Height = 22,
                Font = FormUiKit.BaseFont,
                EnableRelativeToWorkingFrameCoordinates = false,
                ListenToPick = true,
                Margin = new Padding(2, 1, 0, 0)
            };
            PickFocus.Wire(_frameOrigin);
            rowOrigin.Controls.Add(_frameOrigin);
            content.Controls.Add(rowOrigin);

            _exportTabs = new TabControl { Width = 290, Height = 216, Font = FormUiKit.BaseFont };
            var cgrTab = new TabPage("3DXML / CGR");
            var cgrOptions = BuildOptionPanel(cgrTab);
            _reconstructCylinders = new CheckBox { Text = "内外圆柱重建", AutoSize = true,
                Checked = ReadPreference("ReconstructCylinders") == "1", Margin = new Padding(3, 6, 3, 2) };
            _reconstructCylinders.CheckedChanged += (s, e) =>
                WritePreference("ReconstructCylinders", _reconstructCylinders.Checked ? "1" : "0");
            AddExclusiveOptions(cgrOptions, "CgrMode", new[]
            {
                new ExportMode("3dxml", "直出 3DXML", "3DXML"),
                new ExportMode("3dxml-line", "直出 3DXML（R38 线面几何）", "3DXML", CgrBackend.LineFace),
                new ExportMode("ps-cgr", "PS 三角面 → CGR → CATIA", "CGR"),
                new ExportMode("ps-line", "PS 线面压缩 → CGR → CATIA", "CGR", CgrBackend.LineFace),
                new ExportMode("jt-cgr", "JT → CGR → CATIA", "JT_CGR"),
                new ExportMode("jt-line", "JT 线面压缩 → CGR → CATIA", "JT_CGR", CgrBackend.LineFace)
            }, mode =>
            {
                _cgrMode = mode;
                _reconstructCylinders.Enabled = mode.SupportsCylinders;
                UpdateEndModelingAction();
            });
            cgrOptions.Controls.Add(_reconstructCylinders);
            var meshTab = new TabPage("STL / OBJ / PLY / FBX");
            var meshOptions = BuildOptionPanel(meshTab);
            AddExclusiveOptions(meshOptions, "MeshFormat", new[]
            {
                new ExportMode("stl", "STL + RGB 清单", "STL"),
                new ExportMode("obj", "OBJ + MTL 材质", "OBJ"),
                new ExportMode("ply", "PLY（面颜色）", "PLY"),
                new ExportMode("fbx-binary", "FBX 二进制（Blender）", "FBX_BINARY"),
                new ExportMode("fbx-ascii", "FBX ASCII", "FBX_ASCII")
            }, mode => _meshFormat = mode);
            _mergeMeshes = new CheckBox { Text = "所有设备合并为一个网格文件", AutoSize = true,
                Checked = ReadPreference("MergeMeshes") == "1", Margin = new Padding(3, 6, 3, 2) };
            _mergeMeshes.CheckedChanged += (s, e) => WritePreference("MergeMeshes", _mergeMeshes.Checked ? "1" : "0");
            meshOptions.Controls.Add(_mergeMeshes);
            _exportTabs.TabPages.Add(cgrTab);
            _exportTabs.TabPages.Add(meshTab);
            _exportTabs.SelectedIndex = ReadPreference("ExportTab") == "mesh" ? 1 : 0;
            _exportTabs.SelectedIndexChanged += (s, e) =>
            {
                WritePreference("ExportTab", _exportTabs.SelectedIndex == 1 ? "mesh" : "cgr");
                UpdateEndModelingAction();
            };
            content.Controls.Add(_exportTabs);
            FormUiKit.FillWidthInFlow(content, _exportTabs);

            return card;
        }

        private static FlowLayoutPanel BuildOptionPanel(TabPage page)
        {
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
                WrapContents = false, AutoScroll = true, Padding = new Padding(8, 5, 4, 4) };
            page.Controls.Add(panel);
            return panel;
        }

        private void AddExclusiveOptions(FlowLayoutPanel panel, string key, ExportMode[] modes, Action<ExportMode> select)
        {
            var checks = new List<CheckBox>();
            string savedId = ReadPreference(key);
            var initial = Array.Find(modes, mode => mode.Id == savedId) ?? modes[0];
            select(initial);
            foreach (var mode in modes)
            {
                var check = new CheckBox { Text = mode.Label, AutoSize = true, AutoCheck = false,
                    Checked = mode == initial, Margin = new Padding(3, 2, 3, 2) };
                check.Click += (s, e) =>
                {
                    if (check.Checked) return;
                    foreach (var item in checks) item.Checked = item == check;
                    select(mode);
                    WritePreference(key, mode.Id);
                };
                checks.Add(check);
                panel.Controls.Add(check);
            }
        }

        private static string ReadPreference(string name)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(PreferencesKey))
                    return key?.GetValue(name) as string;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                return null;
            }
        }

        private void WritePreference(string name, string value)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(PreferencesKey))
                    key.SetValue(name, value, RegistryValueKind.String);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                Log("保存导出参数失败：" + ex.Message);
            }
        }

        private Control BuildActionRow()
        {
            var actions = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 5,
                RowCount = 1,
                Margin = new Padding(6, 0, 6, 2)
            };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var btnEnum = FormUiKit.MkFuncButton("枚举可见资源", Theme.BtnPrimary);
            btnEnum.Click += (s, e) => EnumerateVisible();
            var btnClear = FormUiKit.MkFuncButton("清空列表", Theme.BtnSecondary);
            btnClear.Click += (s, e) => ClearGrid();
            actions.Controls.Add(btnEnum, 0, 0);
            actions.Controls.Add(btnClear, 1, 0);
            _btnEndModeling = FormUiKit.MkFuncButton("结束建模", Theme.BtnSecondary);
            _btnEndModeling.Click += (s, e) => EndSelectedModeling();
            actions.Controls.Add(_btnEndModeling, 2, 0);

            _btnRun = FormUiKit.MkButton("开始导出", true);
            _btnRun.Anchor = AnchorStyles.Right;
            _btnRun.Click += (s, e) => RunExport();
            actions.Controls.Add(_btnRun, 4, 0);
            UpdateEndModelingAction();

            return actions;
        }

        // ── 事件 ───────────────────────────────────────────────────────
        private void ClearGrid()
        {
            try { if (_objGrid != null) _objGrid.Objects = new TxObjectList(); } catch { }
        }

        private List<ITxObject> GetGridObjects()
        {
            var list = new List<ITxObject>();
            if (_objGrid == null) return list;
            int n = 0;
            try { n = _objGrid.Count; } catch { return list; }
            for (int i = 0; i < n; i++)
            {
                try
                {
                    var o = _objGrid.GetObject(i);
                    if (o != null) list.Add(o);
                }
                catch { }
            }
            return list;
        }

        private TxTransformation GetOriginLocation()
        {
            if (_frameOrigin == null || string.IsNullOrWhiteSpace(_frameOrigin.Text)) return null;
            var location = _frameOrigin.GetLocation();
            if (location == null) throw new InvalidOperationException("请选择或输入有效的参考坐标。");
            return new TxTransformation(location);
        }

        private void EnumerateVisible()
        {
            try
            {
                Log("[PS] 枚举所有可见资源...");
                var devices = _svc.EnumerateVisibleDevices(Log);
                _objGrid.Objects = ToTxObjectList(devices);
                Log("[PS] 已枚举可见设备 " + devices.Count + " 个");
            }
            catch (Exception ex)
            {
                Log("[错误] " + ex.Message);
            }
        }

        private static TxObjectList ToTxObjectList(List<ITxObject> objs)
        {
            var list = new TxObjectList();
            foreach (var o in objs)
                if (o != null) list.Add(o);
            return list;
        }

        private void UpdateEndModelingAction()
        {
            if (_btnEndModeling == null) return;
            bool jt = _exportTabs.SelectedIndex == 0 && _cgrMode?.Format == "JT_CGR";
            _btnEndModeling.Visible = jt;
            _btnEndModeling.Enabled = jt && _btnRun.Enabled;
        }

        private bool TryGetExportResources(out List<ITxObject> picked)
        {
            picked = null;
            if (_chkAllVisible != null && _chkAllVisible.Checked)
            {
                try
                {
                    Log("[PS] 使用全部可见资源...");
                    picked = _svc.EnumerateVisibleDevices(Log);
                }
                catch (Exception ex)
                {
                    Log("[错误] 枚举可见资源失败: " + ex.Message);
                    return false;
                }
                if (picked.Count == 0)
                {
                    Log("[提示] 场景中没有可见资源可导出。");
                    return false;
                }
            }
            else
            {
                picked = GetGridObjects();
                if (picked.Count == 0)
                {
                    Log("[提示] 请先在 PS 中拾取设备/组合。");
                    return false;
                }
            }
            return true;
        }

        private void EndSelectedModeling()
        {
            if (!_btnRun.Enabled || !TryGetExportResources(out var picked)) return;
            bool listenToPick = _objGrid.ListenToPick;
            _btnRun.Enabled = false;
            UpdateEndModelingAction();
            try
            {
                var opened = ExportByColorService.FindOpenJtModelingResources(picked);
                if (opened.Count == 0)
                {
                    Log("[JT] 当前导出范围内没有处于建模状态的资源。");
                    return;
                }
                _objGrid.ListenToPick = false;
                ExportByColorService.EndJtModeling(opened, Log);
            }
            catch (Exception ex)
            {
                Log("[JT] 结束建模失败：" + ex.Message);
            }
            finally
            {
                if (!_closing && !IsDisposed)
                {
                    _objGrid.ListenToPick = listenToPick;
                    _btnRun.Enabled = true;
                    UpdateEndModelingAction();
                }
            }
        }

        private void RunExport()
        {
            if (!TryGetExportResources(out var picked)) return;
            var route = _exportTabs.SelectedIndex == 0 ? _cgrMode : _meshFormat;
            string format = route.Format;
            if (format == "JT_CGR")
            {
                try
                {
                    var opened = ExportByColorService.FindOpenJtModelingResources(picked);
                    if (opened.Count != 0)
                    {
                        Log("[JT] " + opened.Count + " 个资源仍处于建模状态。请点击「结束建模」保存，再开始导出。");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Log("[JT] 检查建模状态失败：" + ex.Message);
                    return;
                }
            }
            TxTransformation originLocation;
            try { originLocation = GetOriginLocation(); }
            catch (Exception ex)
            {
                Log("[参考坐标] " + ex.Message);
                return;
            }
            string origin = originLocation == null ? "" : _frameOrigin.Text.Trim();
            bool reconstructCylinders = route.SupportsCylinders && _reconstructCylinders.Checked;
            CgrBackend cgrBackend = reconstructCylinders ? CgrBackend.LineFace : route.Backend;
            string output = null;
            if (format == "3DXML")
            {
                using (var dialog = new SaveFileDialog
                {
                    Title = "保存直出 3DXML 装配",
                    Filter = "3DXML 装配 (*.3dxml)|*.3dxml",
                    DefaultExt = "3dxml",
                    AddExtension = true,
                    OverwritePrompt = true,
                    FileName = "TxTools_Direct_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".3dxml"
                })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    output = dialog.FileName;
                }
            }
            else if (format != "CGR" && format != "JT_CGR")
            {
                using (var dialog = new FolderBrowserDialog { Description = "选择网格输出目录（将在其中创建本次导出文件夹）" })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    output = dialog.SelectedPath;
                }
            }
            _btnRun.Enabled = false;
            SuspendPicking(true);
            var cancellation = new CancellationTokenSource();
            _exportCancellation = cancellation;
            UpdateEndModelingAction();
            _progress.Value = 0;
            _lastProgressBucket = -1;
            Log("=== 开始导出 " + format + " ===");
            Log("资源: " + picked.Count + " 个, 原点: " + (origin.Length > 0 ? origin : "世界坐标"));
            if (reconstructCylinders && route.Backend != cgrBackend)
                Log("[CGR] 内外圆柱重建已启用，使用支持圆柱面和圆边的线面编码。");
            Action<bool, string> complete = (ok, msg) =>
            {
                try
                {
                    if (_closing || IsDisposed) return;
                    BeginInvoke(new Action(delegate ()
                    {
                        if (_closing || IsDisposed) return;
                        _exportCancellation = null;
                        cancellation.Dispose();
                        _btnRun.Enabled = true;
                        SuspendPicking(false);
                        UpdateEndModelingAction();
                        if (ok) _progress.Value = 100;
                        Log((ok ? "[完成] " : "[错误] ") + msg);
                    }));
                }
                catch { }
            };
            try
            {
                if (format == "JT_CGR")
                {
                    Log("JT 模式：从资源解析 JT 文件生成 CGR；外观读取关联零件三角面并变换到外观位置，再插入 CATIA。");
                    _svc.RunJtToCgrAsync(picked, Log, SetProgress, complete, cgrBackend, reconstructCylinders, originLocation, cancellation.Token);
                }
                else
                {
                    _svc.RunAsync(picked, origin, format, output, _exportTabs.SelectedIndex == 1 && _mergeMeshes.Checked,
                        Log, SetProgress, complete, cgrBackend, reconstructCylinders, originLocation, cancellation.Token);
                }
            }
            catch (Exception ex)
            {
                _exportCancellation = null;
                cancellation.Dispose();
                _btnRun.Enabled = true;
                SuspendPicking(false);
                UpdateEndModelingAction();
                Log("[错误] " + ex.Message);
            }
        }

        private void SetProgress(ExportProgressInfo info)
        {
            if (info == null || _closing || IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<ExportProgressInfo>(SetProgress), info); } catch { }
                return;
            }
            int value = info.Percent;
            if (info.Total > 0 && info.Completed >= info.Total) value = 100;
            _progress.Value = Math.Max(_progress.Minimum, Math.Min(_progress.Maximum, value));
            int bucket = value / 10;
            if (info.Total <= 0 || bucket <= _lastProgressBucket) return;
            _lastProgressBucket = bucket;
            string device = string.IsNullOrEmpty(info.DeviceName) ? "" : " · " + info.DeviceName;
            string failed = info.Failed > 0 ? "，失败 " + info.Failed : "";
            Log("[进度] " + value + "% · " + (info.Stage ?? "处理中") + device + " · 采集 "
                + info.Collected + "/" + info.Total + "，完成 " + info.Completed + "/" + info.Total + failed);
        }

        private void Log(string msg)
        {
            if (_rtbLog == null || _closing || IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string>(Log), msg); } catch { }
                return;
            }
            if (string.Equals(_lastLogMessage, msg, StringComparison.Ordinal)) return;
            _lastLogMessage = msg;
            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + "\n";
            if (_rtbLog.TextLength + line.Length > MaxLogCharacters)
            {
                int remove = Math.Min(_rtbLog.TextLength, Math.Max(1, _rtbLog.TextLength - MaxLogCharacters / 2));
                _rtbLog.Select(0, remove);
                _rtbLog.SelectedText = "[日志已精简；仅保留最近记录]\n";
            }
            _rtbLog.AppendText(line);
            _rtbLog.SelectionStart = _rtbLog.TextLength;
            _rtbLog.ScrollToCaret();
        }
    }
}
