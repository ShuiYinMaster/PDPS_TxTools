using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Tecnomatix.Engineering;
using Tecnomatix.Engineering.Ui;
using TxTools.Common;
using Theme = TxTools.Common.FormUiKit.Theme;
using TextBox = System.Windows.Forms.TextBox;
using Button = System.Windows.Forms.Button;
using Label = System.Windows.Forms.Label;

namespace TxTools.ExportByColor
{
    public class ExportByColorForm : PickAwareTxForm
    {
        internal static ExportByColorForm Instance;

        private readonly SynchronizationContext _psCtx;
        private ExportByColorService _svc;

        private TxObjGridCtrl _objGrid;
        private TxObjEditBoxCtrl _objOrigin;
        private CheckBox _chkAllVisible;
        private TabControl _exportTabs;
        private ComboBox _cgrMode;
        private ComboBox _meshFormat;
        private CheckBox _mergeMeshes;
        private RichTextBox _rtbLog;
        private Button _btnRun;
        private ProgressBar _progress;
        private Label _progressText;
        private string _lastLogMessage;
        private const int MaxLogCharacters = 60000;

        private bool _scaled;
        private readonly Size _designSize = new Size(960, 740);
        private readonly Size _minSize = new Size(740, 540);

        public ExportByColorForm(SynchronizationContext psCtx)
        {
            _psCtx = psCtx;
            SemiModal = false;
            _svc = new ExportByColorService(psCtx);
            FormUiKit.InitStandardForm(this,
                "导出 3DXML / CGR / 通用网格",
                _designSize, _minSize);
            BuildUi();
            Log("插件已启动：在 PS 中拾取对象加入列表，或勾选【导出所有可见资源】");
            Log("默认直出 3DXML：每设备编码为独立 3DRep，并保留 PS 装配层级；无需启动 CATIA。编码器 " + CgrWriter.Version);
        }

        public override void OnInitTxForm()
        {
            base.OnInitTxForm();
            SemiModal = false;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            FormUiKit.ApplyDpiScaling(this, ref _scaled, _designSize);
        }

        private void BuildUi()
        {
            var root = FormUiKit.BuildRoot(null, BuildBody(), BuildBottom());
            Controls.Add(root);
        }

        // ── 主体：顶部两卡(资源/参数) + 底部日志 ────────────────────────
        private Control BuildBody()
        {
            var body = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2
            };
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 38));

            var top = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1
            };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            top.Controls.Add(BuildResourceCard(), 0, 0);
            top.Controls.Add(BuildParamCard(), 1, 0);

            body.Controls.Add(top, 0, 0);

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
            body.Controls.Add(_rtbLog, 0, 1);

            return body;
        }

        private Control BuildResourceCard()
        {
            FlowLayoutPanel content;
            var card = FormUiKit.MkCard("资源（在 PS 中拾取）", 400, 320, out content);

            var hint = FormUiKit.MkLabel("在 PS 场景中点击对象即加入列表（可多选）。勾选【导出所有可见资源】则忽略列表，导出场景全部可见设备。", false);
            FormUiKit.WrapLabelInFlow(content, hint);
            content.Controls.Add(hint);

            _objGrid = new TxObjGridCtrl
            {
                Dock = DockStyle.Fill,
                ListenToPick = true,
                EnableMultipleSelection = true,
                EnableRecurringObjects = false
            };
            // TxObjGridCtrl 拾取焦点统一管理：启动抢焦点 + 点击重获焦点 + ESC 取消焦点
            FormUiKit.GridPickFocus.Wire(_objGrid);
            _objGrid.ObjectInserted += new TxObjGridCtrl_ObjectInsertedEventHandler(OnGridObjectInserted);
            _objGrid.RowDeleted += new TxObjGridCtrl_RowDeletedEventHandler(OnGridRowDeleted);

            var gridPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 170,
                BackColor = SystemColors.Window,
                Padding = new Padding(1),
                Margin = new Padding(0, 2, 0, 4),
                BorderStyle = BorderStyle.FixedSingle
            };
            gridPanel.Controls.Add(_objGrid);
            content.Controls.Add(gridPanel);
            FormUiKit.FillWidthInFlow(content, gridPanel);

            var row = FormUiKit.MkRowFlow();
            _chkAllVisible = new CheckBox
            {
                Text = "导出所有可见资源",
                AutoSize = true,
                Font = FormUiKit.BaseFont,
                Checked = false
            };
            var btnEnum = FormUiKit.MkFuncButton("枚举可见资源", Theme.BtnPrimary);
            btnEnum.Click += (s, e) => EnumerateVisible();
            var btnClear = FormUiKit.MkFuncButton("清空列表", Theme.BtnSecondary);
            btnClear.Click += (s, e) => ClearGrid();
            row.Controls.Add(_chkAllVisible);
            row.Controls.Add(btnEnum);
            row.Controls.Add(btnClear);
            content.Controls.Add(row);

            return card;
        }

        private Control BuildParamCard()
        {
            FlowLayoutPanel content;
            var card = FormUiKit.MkCard("参数", 400, 320, out content);

            var rowOrigin = FormUiKit.MkRowFlow();
            rowOrigin.Controls.Add(FormUiKit.MkFieldLabel("原点设备:"));
            _objOrigin = new TxObjEditBoxCtrl
            {
                Width = 220,
                Height = 22,
                Font = FormUiKit.BaseFont,
                PickOnly = true,
                ListenToPick = true,
                Margin = new Padding(2, 1, 0, 0)
            };
            PickFocus.Wire(_objOrigin);
            _objOrigin.Picked += OnOriginPicked;
            rowOrigin.Controls.Add(_objOrigin);
            content.Controls.Add(rowOrigin);

            _exportTabs = new TabControl { Width = 340, Height = 190, Font = FormUiKit.BaseFont };
            var cgrTab = new TabPage("3DXML / CGR");
            _cgrMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Left = 12, Top = 12, Width = 285, DropDownWidth = 460 };
            _cgrMode.Items.AddRange(new object[] { "直出 3DXML（推荐）", "直出 3DXML（R38线面几何·试用）", "CGR → CATIA（原方案）", "CGR → CATIA（线面压缩·试用）", "CGR → CATIA（逐平面兼容）", "读取 JT → CGR → CATIA", "读取 JT → CGR → CATIA（线面压缩·试用）", "读取 JT → CGR → CATIA（逐平面兼容·试用）", "读取 JT → 内外圆柱重建 CGR → CATIA（试用）" });
            _cgrMode.SelectedIndex = 0;
            cgrTab.Controls.Add(_cgrMode);
            cgrTab.Controls.Add(new Label
            {
                Text = "直出 3DXML 保留装配层级，无需 CATIA。\r\nR38 线面试用沿用其网格分块，再桥接为 CFV3；3DXML 当前不含 CGR 的线、面选择记录。CGR 线面压缩可在 CATIA 中测量；逐平面兼容保留细分面选择。",
                Left = 12,
                Top = 50,
                Width = 300,
                Height = 92,
                AutoSize = false
            });
            var meshTab = new TabPage("STL / OBJ / PLY / FBX");
            _meshFormat = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Left = 12, Top = 12, Width = 260 };
            _meshFormat.Items.AddRange(new object[] { "STL + RGB 清单", "OBJ + MTL 材质", "PLY（面颜色）", "FBX 二进制（Blender）", "FBX ASCII（兼容工具）" });
            _meshFormat.SelectedIndex = 0;
            meshTab.Controls.Add(_meshFormat);
            _mergeMeshes = new CheckBox { Text = "所有设备合并为一个网格文件", AutoSize = true, Left = 12, Top = 45, Font = FormUiKit.BaseFont };
            meshTab.Controls.Add(_mergeMeshes);
            meshTab.Controls.Add(new Label { Text = "按设备名称保存到同一目录，同名追加流水号。STL 配套 RGB 清单；Blender 请选择二进制 FBX。", Dock = DockStyle.Bottom, Padding = new Padding(12, 0, 12, 0), Height = 60 });
            _exportTabs.TabPages.Add(cgrTab);
            _exportTabs.TabPages.Add(meshTab);
            _exportTabs.SelectedIndex = 0;
            content.Controls.Add(_exportTabs);
            FormUiKit.FillWidthInFlow(content, _exportTabs);

            return card;
        }

        private Control BuildBottom()
        {
            var bottom = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1
            };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));

            var activity = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0, 3, 8, 3)
            };
            activity.RowStyles.Add(new RowStyle(SizeType.Absolute, 16));
            activity.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _progressText = new Label
            {
                Dock = DockStyle.Fill,
                Text = "就绪",
                ForeColor = Theme.TextDim,
                Font = new Font("Microsoft YaHei UI", 8.25F),
                TextAlign = ContentAlignment.MiddleLeft
            };
            _progress = new ProgressBar
            {
                Dock = DockStyle.Fill,
                Minimum = 0,
                Maximum = 100,
                Margin = new Padding(0, 0, 0, 1)
            };
            activity.Controls.Add(_progressText, 0, 0);
            activity.Controls.Add(_progress, 0, 1);
            bottom.Controls.Add(activity, 0, 0);

            _btnRun = FormUiKit.MkButton("开始导出", true, 130, 34);
            _btnRun.Click += (s, e) => RunExport();
            bottom.Controls.Add(_btnRun, 1, 0);

            return bottom;
        }

        // ── 事件 ───────────────────────────────────────────────────────
        private void OnGridObjectInserted(object sender, TxObjGridCtrl_ObjectInsertedEventArgs e)
        {
        }

        private void OnGridRowDeleted(object sender, TxObjGridCtrl_RowDeletedEventArgs e)
        {
        }

        private void OnOriginPicked(object sender, EventArgs e)
        {
        }

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

        private string GetOriginName()
        {
            if (_objOrigin == null) return "";
            try
            {
                var o = _objOrigin.Object as ITxObject;
                if (o != null) return o.Name;
            }
            catch { }
            return "";
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

        private void RunExport()
        {
            List<ITxObject> picked;
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
                    return;
                }
                if (picked.Count == 0)
                {
                    MessageBox.Show(this, "场景中没有可见资源可导出。", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            else
            {
                picked = GetGridObjects();
                if (picked.Count == 0)
                {
                    MessageBox.Show(this, "请先在 PS 中拾取设备/组合。", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            string origin = GetOriginName();
            string format = _exportTabs.SelectedIndex == 0
                ? (_cgrMode.SelectedIndex <= 1 ? "3DXML" : (_cgrMode.SelectedIndex >= 5 ? "JT_CGR" : "CGR"))
                : new[] { "STL", "OBJ", "PLY", "FBX_BINARY", "FBX_ASCII" }[_meshFormat.SelectedIndex];
            CgrBackend cgrBackend = format == "3DXML"
                ? (_cgrMode.SelectedIndex == 1 ? CgrBackend.LineFace : CgrBackend.Compact)
                : (format == "CGR" ? (_cgrMode.SelectedIndex == 3 ? CgrBackend.LineFace : _cgrMode.SelectedIndex == 4 ? CgrBackend.LineFacePlanar : CgrBackend.Compact) : CgrBackend.Compact);
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
            _progress.Value = 0;
            SetProgress(new ExportProgressInfo { Stage = "正在准备导出" });
            Log("=== 开始导出 " + format + " ===");
            Log("资源: " + picked.Count + " 个, 原点: " + (origin.Length > 0 ? origin : "世界坐标"));
            Action<bool, string> complete = (ok, msg) =>
            {
                try
                {
                    if (IsDisposed) return;
                    BeginInvoke(new Action(delegate ()
                    {
                        _btnRun.Enabled = true;
                        if (ok) _progress.Value = 100;
                        if (_progressText != null)
                            _progressText.Text = ok ? "导出完成" : "导出失败：请查看下方摘要";
                        if (ok) Log("[完成] " + msg);
                        else Log("[错误] " + msg);
                        MessageBox.Show(this, msg, ok ? "完成" : "失败",
                            MessageBoxButtons.OK,
                            ok ? MessageBoxIcon.Information : MessageBoxIcon.Error);
                    }));
                }
                catch { }
            };
            if (format == "JT_CGR")
            {
                Log("JT 模式：从资源 StorageObject/表示属性解析 JT 文件，再生成 CGR 并插入 CATIA。");
                _svc.RunJtToCgrAsync(picked, Log, SetProgress, complete, (_cgrMode.SelectedIndex == 6 || _cgrMode.SelectedIndex == 8) ? CgrBackend.LineFace : _cgrMode.SelectedIndex == 7 ? CgrBackend.LineFacePlanar : CgrBackend.Compact, _cgrMode.SelectedIndex == 8);
            }
            else
            {
                _svc.RunAsync(picked, origin, format, output, format != "CGR" && format != "3DXML" && _mergeMeshes.Checked, Log, SetProgress, complete, cgrBackend);
            }
        }

        private void SetProgress(ExportProgressInfo info)
        {
            if (info == null || IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<ExportProgressInfo>(SetProgress), info); } catch { }
                return;
            }
            int value = info.Percent;
            if (info.Total > 0 && info.Completed >= info.Total) value = 100;
            _progress.Value = Math.Max(_progress.Minimum, Math.Min(_progress.Maximum, value));
            if (_progressText == null) return;
            if (info.Total <= 0)
            {
                _progressText.Text = string.IsNullOrEmpty(info.Stage) ? "正在准备" : info.Stage;
                return;
            }
            string device = string.IsNullOrEmpty(info.DeviceName) ? "" : " · " + info.DeviceName;
            string failed = info.Failed > 0 ? "，失败 " + info.Failed : "";
            _progressText.Text = (info.Stage ?? "处理中") + device + "  |  采集 " + info.Collected + "/" + info.Total + "，完成 " + info.Completed + "/" + info.Total + failed;
        }

        private void Log(string msg)
        {
            if (_rtbLog == null || IsDisposed) return;
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
