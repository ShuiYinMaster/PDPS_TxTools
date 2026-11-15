using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Tecnomatix.Engineering;
using Tecnomatix.Engineering.Ui;
using TxTools.Agent.Core;
using TxTools.Common;

namespace TxTools.CrossEnvIO
{
    internal sealed class CrossEnvPickResult
    {
        public string Id, Purpose, State = "waiting", Study, Document, StudyId, ParentId, ParentName, RowKey;
        public List<string> Parts = new List<string>();
        public double[] WorldFrame;
    }

    /// <summary>场景对象只在所属进程中用原生拾取框选择，调用方只接收经核验的对象标识。</summary>
    internal sealed class CrossEnvPickForm : PickAwareTxForm
    {
        private static readonly Dictionary<string, CrossEnvPickForm> Active = new Dictionary<string, CrossEnvPickForm>();
        private static readonly Dictionary<string, CrossEnvPickResult> Results = new Dictionary<string, CrossEnvPickResult>();
        private readonly CrossEnvPickResult _result;
        private readonly TxDocument _document;
        private readonly TxObjEditBoxCtrl _single;
        private readonly TxObjGridCtrl _multiple;
        private readonly TxFrameEditBoxCtrl _frame;
        private bool _frameInvalid;
        private readonly GroupBox _pickerFrame;
        private readonly Label _status;
        private bool _scaled;
        private Size DesignSize => new Size(430, _result.Purpose == "binding" ? 380 : 270);
        internal static JObject Request(JObject request)
        {
            string id = (string)request["id"];
            if (string.IsNullOrEmpty(id) || id.Length > 80) throw new ArgumentException("缺少选取请求标识");
            CrossEnvPickResult result;
            if (Results.TryGetValue(id, out result)) return JObject.FromObject(result);
            if (Active.Count >= 4) throw new InvalidOperationException("接收端存在未处理的选取请求，请先处理或关闭");
            var form = new CrossEnvPickForm(id, (string)request["purpose"] ?? "parent", (string)request["row_key"], (string)request["message"]);
            Active.Add(id, form); Results.Add(id, form._result); form.Show(); form.Activate();
            return JObject.FromObject(form._result);
        }
        internal static JObject Status(string id)
        {
            CrossEnvPickResult value;
            return Results.TryGetValue(id ?? "", out value) ? JObject.FromObject(value)
                : new JObject { ["State"] = "unknown", ["Error"] = "请求不存在，不能据此确认已选对象" };
        }

        private CrossEnvPickForm(string id, string purpose, string rowKey, string message)
        {
            if (purpose != "parent" && purpose != "binding" && purpose!="frame") throw new ArgumentException("未知原生拾取用途");
            _document = TxApplication.ActiveDocument ?? throw new InvalidOperationException("接收端没有活动工程");
            ShowInTaskbar=true;
            _result = new CrossEnvPickResult { Id = id, Purpose = purpose, RowKey = rowKey,
                Study = _document.CurrentStudy.Name, Document = _document.FinalDestination ?? "", StudyId = TransferCapture.StudyId() };
            SemiModal = false;
            FormUiKit.InitStandardForm(this, purpose == "frame"?"跨环境：选取接收端参考坐标":purpose == "parent" ? "跨环境：确认接收父容器" : "跨环境：确认焊点绑定零件",
                DesignSize, new Size(390,purpose=="binding"?350:270), sizable: true);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(12) };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(390, 0), Font = FormUiKit.BaseFont,
                Text = _result.Study + "\n" + (message ?? (purpose == "parent" ? "单选父容器；不指定则使用文档根。" : "为此焊点多选全部绑定零件。")) + (purpose == "binding" ? "\n列表首个零件用作主板。" : "") }, 0, 0);
            if(purpose=="frame")
            {
                _frame=new TxFrameEditBoxCtrl { Height=28,Font=FormUiKit.BaseFont };
                PickFocus.Wire(_frame);_frame.ValidFrameSet+=(s,e)=>_frameInvalid=false;_frame.InvalidFrameSet+=(s,e)=>_frameInvalid=true;
                _pickerFrame=Frame("接收端参考坐标 · 原生坐标选取",_frame);_pickerFrame.Dock=DockStyle.Fill;
                layout.Controls.Add(_pickerFrame,0,1);
            }
            else if (purpose == "parent")
            {
                _single = new TxObjEditBoxCtrl { Dock = DockStyle.Top, Height = 26, Font = FormUiKit.BaseFont, ListenToPick = true };
                PickFocus.Wire(_single); _pickerFrame=Frame("目标父容器 · 单选",_single); _pickerFrame.Dock=DockStyle.Fill;
                layout.Controls.Add(_pickerFrame, 0, 1);
            }
            else
            {
                _multiple = new TxObjGridCtrl { Dock = DockStyle.Fill, MinimumSize = new Size(0, NativeGridHeight(FormUiKit.BaseFont)),
                    Height = NativeGridHeight(FormUiKit.BaseFont), Font = FormUiKit.BaseFont, ListenToPick = true,
                    EnableMultipleSelection = true, EnableRecurringObjects = false };
                PickFocus.Wire(_multiple); _pickerFrame=Frame("绑定零件 · 多选",_multiple); _pickerFrame.Dock=DockStyle.Fill;
                layout.Controls.Add(_pickerFrame, 0, 1);
            }
            _status = new Label { AutoSize = true, MaximumSize = new Size(390, 0), Font = FormUiKit.BaseFont };
            layout.Controls.Add(_status, 0, 2);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
            var confirm = FormUiKit.MkBtn("确认并返回调用端", FormUiKit.Theme.BtnPrimary, 156, 30);
            confirm.Click += (s, e) => Confirm(); actions.Controls.Add(confirm);
            var cancel = FormUiKit.MkBtn("取消", FormUiKit.Theme.BtnSecondary, 70, 30); cancel.Click += (s, e) => Close(); actions.Controls.Add(cancel);
            layout.Controls.Add(actions, 0, 3); Controls.Add(layout);
            FormClosed += (s, e) =>
            {
                LibraryPersistence.Reloading-=CancelForReload;
                if (_result.State == "waiting") _result.State = "cancelled";
                Active.Remove(id);
                if (Results.Count > 256)
                    foreach (string key in Results.Where(p => p.Value.State != "waiting").Select(p => p.Key).Take(64).ToList()) Results.Remove(key);
            };
            Shown += (s, e) => FitGrid();
            LibraryPersistence.Reloading+=CancelForReload;
            DpiChanged += (s, e) => FitGrid();
        }

        protected override void OnLoad(EventArgs e)
        { base.OnLoad(e); FormUiKit.ApplyDpiScaling(this, ref _scaled, DesignSize); FitGrid(); }
        private void CancelForReload(){_result.State="cancelled";Close();}
        private void FitGrid()
        {
            ((CrossEnvNativeFrame)_pickerFrame).RefreshMetrics(DeviceDpi);
            if (_multiple == null) return;
            MinimumSize = new Size(MinimumSize.Width, Math.Max(MinimumSize.Height, _pickerFrame.MinimumSize.Height + (int)(170 * DeviceDpi / 96.0)));
        }
        internal static GroupBox Frame(string title,Control picker)
        {
            return new CrossEnvNativeFrame(title,picker);
        }
        internal static int NativeGridHeight(Font font, int dpi = 96)
        {
            double scale = Math.Max(1, dpi / 96.0);
            return (int)Math.Ceiling(32 * scale + 5 * Math.Max(27 * scale, font.Height + 10 * scale));
        }

        private void Confirm()
        {
            try
            {
                if (!Equals(_document, TxApplication.ActiveDocument) || _result.StudyId != TransferCapture.StudyId())
                    throw new InvalidOperationException("工程已切换，请取消并重新请求");
                if(_frame!=null)
                {
                    if(_frameInvalid)throw new InvalidOperationException("参考坐标无效，请重新选取。");
                    _result.WorldFrame=TxMath.WorldValues(_frame.GetLocation()??new TxTransformation());
                }
                else if (_single != null)
                {
                    var selected = _single.Object as ITxObject;
                    _result.ParentId = TransferRuntime.Parent(selected?.Id).Id;
                    _result.ParentName = selected?.Name ?? "文档根";
                }
                else
                {
                    var ids = new HashSet<string>();
                    var ordered = new List<string>();
                    for (int i = 0; i < _multiple.Count; i++)
                    {
                        var part = _multiple.GetObject(i) as TxComponent;
                        if (part == null) throw new InvalidOperationException("绑定列表只能包含组件零件，不能选取几何面或操作");
                        if (ids.Add(part.Id)) ordered.Add(part.Id);
                    }
                    if (ids.Count == 0) throw new InvalidOperationException("请用原生多选框选择至少一个绑定零件");
                    _result.Parts = ordered;
                }
                _result.State = "confirmed"; Close();
            }
            catch (Exception ex) { _status.Text = ex.Message; }
        }
    }

    public sealed class CrossEnvPickRequestTool : TxAgentToolBase
    {
        public override string Name => "crossenv_pick_request";
        public override string Description => "在当前 PS 实例打开 SDK 原生坐标选取框、单对象框或至少五行的焊点绑定框，等待确认。";
        public override bool IsReadOnly => false;
        public override JObject InputSchema => JObject.Parse("{'type':'object','properties':{'id':{'type':'string'},'purpose':{'enum':['parent','binding','frame']},'row_key':{'type':'string'},'message':{'type':'string'}},'required':['id','purpose']}");
        public override string Execute(JObject input)
        { try { return CrossEnvPickForm.Request(input).ToHostJson(Formatting.None); } catch (Exception ex) { return "Error: " + ex.Message; } }
    }
    public sealed class CrossEnvPickStatusTool : TxAgentToolBase
    {
        public override string Name => "crossenv_pick_status";
        public override string Description => "只读查询接收端原生拾取的确认结果，返回所属研究与对象标识。";
        public override bool IsReadOnly => true;
        public override JObject InputSchema => JObject.Parse("{'type':'object','properties':{'id':{'type':'string'}},'required':['id']}");
        public override string Execute(JObject input) => CrossEnvPickForm.Status((string)input["id"]).ToHostJson(Formatting.None);
    }
}
