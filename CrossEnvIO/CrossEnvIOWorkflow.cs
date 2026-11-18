using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Tecnomatix.Engineering;
using TxTools.Agent.Core;
using TxTools.Common;

namespace TxTools.CrossEnvIO
{
    public partial class CrossEnvIOForm
    {
        private TransferPlan _plan;
        private JObject _description;
        private int _confirmedPid = -1;
        private string _pickId, _pickRow, _pickPurpose;
        private string _pickReceiverScope;
        private bool _sourceDirty = true, _frameInvalid, _sourceFrameInvalid, _sourceFromFile;
        private string _submittedFingerprint;
        private string _selectionSignature,_sourceCaptureError;
        private CancellationTokenSource _cancellation;
        private readonly List<PsInstanceInfo> _peerList = new List<PsInstanceInfo>();
        private readonly string _session = TxToolsTemp.SessionDirectory("CrossEnvIO", "transfer");
        private string PendingFile => TxToolsTemp.FileFor("CrossEnvIO", "pending-" + PsInstanceRegistry.SelfPid + ".json");
        private static CopyResult _copies;
        private static TransferPlan _lastModel;
        private static TransferJob _lastModelJob;
        private static int _lastModelPid;
        private readonly List<BindingIssue> _bindingIssues = new List<BindingIssue>();
        private sealed class BindingIssue
        {
            public string Key, Message, DisplayText, ReceiverScope;
            public override string ToString() => DisplayText ?? Message;
        }
        private TransferPackage CurrentPackage => _plan != null && _package.SelectedIndex >= 0
            && _package.SelectedIndex < _plan.Packages.Count ? _plan.Packages[_package.SelectedIndex] : null;

        private void Log(string message)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke(new Action<string>(Log), message); } catch { } return; }
            _log.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + Environment.NewLine);
            _log.SelectionStart = _log.TextLength; _log.ScrollToCaret();
        }
        private async void Run(Func<Task> action, bool allowUncertain = false) => await Work(action, allowUncertain);
        private async Task<bool> Work(Func<Task> action, bool allowUncertain = false)
        {
            if (_busy) return false;
            if (_uncertain && !allowUncertain) { Log("任务结果待确认，请先查询状态。"); return false; }
            // 部分宿主入口没有安装 WinForms 上下文；后台 IO 完成后仍须回到窗体线程调用 SDK。
            if (SynchronizationContext.Current == null || SynchronizationContext.Current.GetType() == typeof(SynchronizationContext))
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            _busy = true; UpdateActions();
            foreach (var picker in new Control[] { _objects, _sourceOrigin, _origin }) PickFocus.SetSuspended(picker, true);
            try { await action(); return true; }
            catch (OperationCanceledException) { _status.Text = "复制已取消，场景尚未修改。"; Log(_status.Text); return false; }
            catch (Exception ex) { ShowFailure(ex); return false; }
            finally
            {
                _cancellation?.Dispose(); _cancellation = null; _cancel.Enabled = false;
                _busy = false; UpdateActions();
                foreach (var picker in new Control[] { _objects, _sourceOrigin, _origin }) PickFocus.SetSuspended(picker, false);
                if(_closeRequested&&!IsDisposed)BeginInvoke(new Action(Close));
            }
        }
        private void InvalidatePlan()
        {
            if (_populating || _uncertain) return;
            _finishedJob = false;
            _submittedFingerprint=null;
            if (_plan != null) { _plan.PreparedStamp = null; _plan.TargetSceneStamp = null; _plan.JobId = Guid.NewGuid().ToString("N"); }
            if (!_busy && _summary != null) _summary.Text = "尚未检查；配置变化后需要重新检查。";
            if (_status != null && !_busy) _status.Text = "";
            RefreshStorageSources();
            UpdateDestination();
            UpdateActions();
        }
        private void SourceChanged()
        {
            if (_populating) return;
            _selectionSignature=SceneSelectionSignature();_sourceCaptureError=null;
            _sourceDirty = true; _sourceFromFile = false; InvalidatePlan();
            if (_scope != null) _scope.Text = _whole.Checked ? "传输整个场景。" : _objects.Count > 0
                ? "已选择 " + _objects.Count + " 个对象。" : "在场景中选取要传输的对象。";
            if (_issues != null && !_uncertain){_issues.Items.Clear();_issues.Visible=false;_issueDetail.Visible=false;UpdateActions();}
            _capturePending=true;_contextTimer.Stop();_contextTimer.Start();
        }
        private List<ITxObject> ReadSceneSelection()
        {
            var result=new List<ITxObject>();
            foreach(ITxObject item in _objects.Objects)if(item!=null)result.Add(item);
            return result;
        }
        private string SceneSelectionSignature()=>_kind.SelectedIndex+"|"+_whole.Checked+"|"+TransferCapture.StudyId()+"|"
            +string.Join("|",ReadSceneSelection().Select(o=>o.Id).OrderBy(id=>id,StringComparer.Ordinal));
        private void ObserveSceneSelection()
        {
            if(_busy||_populating||_uncertain||_sourceFromFile||_task.SelectedIndex==1)return;
            try
            {
                string signature=SceneSelectionSignature();
                if(signature!=_selectionSignature)SourceChanged();
            }
            catch(Exception ex)
            {
                if(_selectionSignature=="unreadable")return;
                _selectionSignature="unreadable";_sourceDirty=true;_sourceCaptureError="读取原生选取内容失败："+ex.Message;
                _capturePending=false;ShowFailure(new InvalidOperationException(_sourceCaptureError,ex));
            }
        }
        private async Task RefreshContext()
        {
            bool capture=_capturePending,receiver=_receiverPending;_capturePending=_receiverPending=false;
            if(capture&&!_sourceFromFile)
            {
                if(_objects.Count>0||_whole.Checked)EnsureCaptured();
                else{_storageSources.Rows.Clear();_scope.Text="尚未选择传输对象。";}
            }
            if(receiver&&SelectedPid()>=0)await ConfirmReceiver();
            if(_description!=null&&(_kind.SelectedIndex==0||_includeWeldParts.Checked)&&_libraryTree.Nodes.Count==0)await LoadLibraryTree();
            FitColumns();ExpandForContent();
        }
        private void NeedPlan()
        {
            if (_plan == null || _sourceDirty) throw new InvalidOperationException("请先选取对象，或从“文件”导入数据");
        }
        private async Task Transfer()
        {
            if (_finishedJob)
            {
                if (_jobSucceeded) return;
                // 使用新的任务编号重新检查，原失败任务及文件依赖记录保留。
                InvalidatePlan(); _issues.Items.Clear();
            }
            if (_pickId != null) throw new InvalidOperationException("请先在接收工程确认或取消选取");
            _settingsWindow.Hide();
            _main.AutoScrollPosition = Point.Empty;
            if (SelectedPid() < 0) throw new InvalidOperationException("请先选择接收工程，再检查重建条件");
            if (_plan != null && !_sourceDirty && !string.IsNullOrEmpty(_plan.PreparedStamp) && _plan.PreparedStamp == _plan.Stamp())
            { await ExecutePlan(); return; }
            if (_task.SelectedIndex == 1 && !_sourceFromFile) throw new InvalidOperationException("请先打开需要重建的数据文件");
            EnsureCaptured(true);
            if (_description == null || _confirmedPid != SelectedPid()) await ConfirmReceiver();
            await Preflight();
        }
        private async void OpenOptions(bool storage)
        {
            if (_busy || _uncertain) return;
            bool ready = await Work(async () =>
            {
                if ((_objects.Count > 0 || _whole.Checked || _plan != null) && _sourceDirty) EnsureCaptured();
                if (SelectedPid() >= 0 && _description == null) await ConfirmReceiver();
            });
            if (!ready || IsDisposed) return;
            if (storage) _options.SelectedIndex = 0;
            RefreshStorageSources();
            ShowSettingsWindow(); UpdateActions(); FitColumns();
        }
        private void ReceiverChanged()
        {
            ClearReceiverBindings();
            _description = null; _confirmedPid = -1;
            _pickId = null; _pickTimer.Stop();
            _libraryDirectories.Clear();if(_plan!=null)_plan.LibraryDirectories.Clear();
            _receiver.Text = SelectedPid() >= 0 ? "检查时自动读取接收工程与库目录。" : "请选择接收工程。";
            _libraryTree.Nodes.Clear();_receiverPending=SelectedPid()>=0;_contextTimer.Start();
            InvalidatePlan(); UpdateKind();
        }
        private void ClearReceiverBindings()
        {
            if(_plan!=null){_plan.WeldBindings.Clear();_plan.ObjectMap.Clear();_plan.ReceiverBindingScope=null;_plan.TargetParentId=null;}
            _bindingIssues.Clear();_issues.Items.Clear();_issues.Visible=false;_issueDetail.Visible=false;
            _pickId=_pickRow=_pickPurpose=_pickReceiverScope=null;_pickTimer.Stop();
            bool previous=_populating;_populating=true;
            try{_origin.SetLocation(new TxTransformation());_frameInvalid=false;if(_plan!=null)_plan.TargetFrame=null;}
            finally{_populating=previous;}
        }
        private static string ReceiverScope(int pid,JObject description)
        {
            if(pid<0||description==null)return null;
            return new JArray(pid,(string)description["study"],(string)description["study_id"],
                ((string)description["document"]??"").Replace('/','\\').ToUpperInvariant(),
                ((string)description["systemRoot"]??"").Replace('/','\\').TrimEnd('\\').ToUpperInvariant()).ToHostJson(Formatting.None);
        }
        private void ApplyPreferences(TransferPlan plan, bool keepPackages = true)
        {
            plan.Placement = (TransferPlacement)Math.Max(0, _placement.SelectedIndex);
            plan.Classification = new[] { "sdk", "planning", "prototype", "family" }[Math.Max(0, _basis.SelectedIndex)];
            plan.SingleDirectory = _singleDir.Text; plan.DefaultDirectory = _fallback.Text;
            plan.LibraryDirectories=new List<string>(_libraryDirectories);
            plan.CopyModels=_copy.Checked;plan.BindOnly=_bindOnly.Checked;plan.Project=_project.Checked;plan.AllowUnboundWelds=true;
            if(!_sourceFrameInvalid&&(keepPackages||plan.SourceFrame==null))plan.SourceFrame=TxMath.WorldValues(_sourceOrigin.GetLocation()??new TxTransformation());
            if(!_frameInvalid&&(keepPackages||plan.TargetFrame==null))plan.TargetFrame=TxMath.WorldValues(_origin.GetLocation()??new TxTransformation());
            if (_plan == null) return;
            plan.TypeDirectories = new Dictionary<string, string>(_plan.TypeDirectories);
            if (!keepPackages) return;
            if (_plan.DataHash == plan.DataHash&&_plan.ReceiverBindingScope!=null&&_plan.ReceiverBindingScope==ReceiverScope(_confirmedPid,_description))
            {
                plan.WeldBindings = _plan.WeldBindings.ToDictionary(p => p.Key, p => new List<string>(p.Value));
                plan.ReceiverBindingScope=_plan.ReceiverBindingScope;
            }
            foreach (var item in plan.Packages)
            {
                var previous = _plan.Packages.FirstOrDefault(p => TransferPaths.Same(p.OriginalSource, item.OriginalSource));
                if (previous == null) continue;
                item.OverrideDirectory = previous.OverrideDirectory; item.TargetName = previous.TargetName;
                item.Conflict = previous.Conflict; item.TargetHash = previous.TargetHash;
                if (!TransferPaths.Same(previous.Source, previous.OriginalSource))
                { item.Source = previous.Source; item.SourceHash = previous.SourceHash; }
            }
        }
        private void EnsureCaptured(bool refresh = false)
        {
            _capturePending=false;
            if (_task.SelectedIndex==1 && !_sourceFromFile)
                throw new InvalidOperationException("请先打开需要重建的数据文件。");
            if (_plan != null && !_sourceDirty && (!refresh || _sourceFromFile)) return;
            var objects = ReadSceneSelection();
            var file = Path.Combine(_session, Guid.NewGuid().ToString("N") + ".tsv");
            var plan = TransferCapture.Capture(objects, _whole.Checked, _kind.SelectedIndex == 1, file, Log,_includeWeldParts.Checked);
            ApplyPreferences(plan); SetPlan(plan);
        }
        private void SetPlan(TransferPlan plan,bool restoreFrames=false)
        {
            _populating = true;
            try
            {
                _plan = plan; _sourceDirty = false;_sourceCaptureError=null;
                _kind.SelectedIndex = plan.Kind == "weld" ? 1 : 0;
                _bindOnly.Visible = _project.Visible = plan.Kind == "weld";
                if(restoreFrames)_includeWeldParts.Checked=plan.Kind=="weld"&&plan.IncludeWeldParts;
                _bindOnly.Checked=plan.BindOnly;_project.Checked=plan.Project;_copy.Checked=plan.CopyModels;
                // Passive selection recognition must not reinitialize native coordinate pickers.
                if(restoreFrames&&plan.SourceFrame!=null)_sourceOrigin.SetLocation(TxMath.FromWorldValues(plan.SourceFrame));
                if(restoreFrames&&plan.TargetFrame!=null)_origin.SetLocation(TxMath.FromWorldValues(plan.TargetFrame));
                foreach (Control control in _storagePage.Controls)
                    control.Visible = plan.Kind == "model" || _storagePage.Controls.GetChildIndex(control) < 2;
                _advancedStorageToggle.Visible=false;
                _storageAdvanced.Visible=(plan.Kind=="model"||_includeWeldParts.Checked)&&_advancedStorageToggle.Checked;
                _placement.SelectedIndex = (int)plan.Placement;
                _basis.SelectedIndex = Array.IndexOf(new[] { "sdk", "planning", "prototype", "family" }, plan.Classification);
                if (_basis.SelectedIndex < 0) _basis.SelectedIndex = 0;
                _singleDir.Text = plan.SingleDirectory; _fallback.Text = plan.DefaultDirectory;
                _package.Items.Clear();
                foreach (var item in plan.Packages) _package.Items.Add(item.Name + " · " + item.Instances + " 实例");
                if (_package.Items.Count > 0) _package.SelectedIndex = 0;
                _scope.Text = plan.Kind == "weld" ? "共 " + plan.ObjectRows + " 个焊点"+(plan.IncludeWeldParts?"；携带 "+plan.Packages.Count+" 个模型包、"+plan.Packages.Sum(p=>p.Instances)+" 个绑定零件实例":"；允许无绑定零件传输")
                    : plan.Packages.Count > 0 ? "已识别 " + plan.Packages.Count + " 个模型文件、"+plan.Packages.Sum(p=>p.Instances)+" 个场景实例；共用文件只复制一次，实例分别重建。"
                    : "仅传输结构和位姿，无模型文件需要复制。";
            }
            finally { _populating = false; }
            ShowPackage(); BuildTypeRules(); UpdateDestination(); UpdateKind(); InvalidatePlan(); Log(_scope.Text);
            UpdateGuidedSource();
        }
        private async Task ImportData()
        {
            using (var dialog = new OpenFileDialog { Filter = "结构 / 焊点数据 / 传输包|*.tsv;*.txt;*.cxio;*.zip", CheckFileExists = true })
            {
                if (dialog.ShowDialog(DialogOwner) != DialogResult.OK) return;
                string file = dialog.FileName;
                TransferPlan plan;
                if (new[] { ".cxio", ".zip" }.Contains(Path.GetExtension(file).ToLowerInvariant()))
                    plan = await Task.Run(() => TransferBundle.Import(file, _session, Log));
                else
                {
                    // 使用本会话拥有的不可变数据快照，避免后续覆盖外部文件。
                    string snapshot = Path.Combine(_session, Guid.NewGuid().ToString("N") + ".tsv");
                    await Task.Run(() => File.Copy(file, snapshot, false));
                    var table = TransferTable.Read(snapshot);
                    plan = TransferCapture.FromFile(snapshot, table.Header.Contains("WeldOpName") ? "weld" : "model");
                }
                ApplyPreferences(plan, false); SetPlan(plan,true); _sourceFromFile = true;
                _populating = true;
                try { _objects.Objects = new TxObjectList(); _whole.Checked = false; }
                finally { _populating = false; }
                _scope.Text = "已载入：" + Path.GetFileName(file) + "（" + plan.ObjectRows + " 个节点）";
                UpdateGuidedSource(file);
            }
        }
        private async Task ExportData(bool bundle)
        {
            EnsureCaptured(true);
            if(_sourceFrameInvalid||_frameInvalid)throw new InvalidOperationException("参考坐标无效，请重新选取后导出。");
            if(!bundle&&_plan.Kind=="weld"&&_plan.IncludeWeldParts)
                throw new InvalidOperationException("TSV 只包含焊点数据；要一起传输绑定零件，请使用“导出离线包”。");
            using (var dialog = new SaveFileDialog { Filter = bundle ? "离线传输包|*.cxio" : "TSV 数据|*.tsv",
                FileName = bundle ? "CrossEnv.cxio" : "CrossEnv.tsv", AddExtension = true, OverwritePrompt = true })
            {
                if (dialog.ShowDialog(DialogOwner) != DialogResult.OK) return;
                string output = dialog.FileName;
                if (TransferPaths.Same(output, _plan.DataFile)) throw new IOException("导出位置不能覆盖当前数据快照");
                if (bundle) await Task.Run(() => TransferBundle.Export(_plan, output, Log));
                else await Task.Run(() => TransferFile.Write(output, File.ReadAllText(_plan.DataFile, Encoding.UTF8)));
                _status.Text = "已导出 · " + Path.GetFileName(output);
                Log("已导出：" + output);
            }
        }
        private string _peerSnapshot;
        private void RefreshPeers() => RefreshPeersCore(false);
        private void RefreshPeersCore(bool automatic)
        {
            if (_uncertain || _busy) return;
            int previous = SelectedPid();
            var live = PsInstanceRegistry.Live().Where(p => !p.IsSelf).ToList();
            string snapshot = string.Join("|", live.OrderBy(p => p.Pid).Select(p => p.Pid + ":" + p.Name));
            if (automatic && snapshot == _peerSnapshot) return;
            _peerSnapshot = snapshot;
            _populating = true;
            try
            {
                _peers.Items.Clear(); _peerList.Clear(); _peers.Items.Add("请选择接收工程…");
                foreach (var peer in live) { _peerList.Add(peer); _peers.Items.Add(peer.ToString()); }
                _peerList.Add(new PsInstanceInfo { Pid = PsInstanceRegistry.SelfPid, Name = "当前工程（本实例）" });
                _peers.Items.Add("当前工程（本实例）");
                int index = _peerList.FindIndex(p => p.Pid == previous);
                _peers.SelectedIndex = _task.SelectedIndex == 1 ? _peerList.Count : index >= 0 ? index + 1 : live.Count == 1 ? 1 : 0;
            }
            finally { _populating = false; }
            Log("发现 " + live.Count + " 个可接收工程。");
            if(previous!=SelectedPid())ReceiverChanged();
            else if(SelectedPid()>=0){_receiverPending=true;_contextTimer.Start();}
        }
        private int SelectedPid() => _peers.SelectedIndex > 0 && _peers.SelectedIndex <= _peerList.Count
            ? _peerList[_peers.SelectedIndex - 1].Pid : -1;
        private PsInstanceInfo RemotePeer(int pid)
        {
            return PsInstanceRegistry.Live().FirstOrDefault(p => p.Pid == pid && !p.IsSelf)
                ?? throw new InvalidOperationException("已确认的接收实例不可用，请恢复该实例后查询；不会改发其他工程");
        }
        private async Task<JObject> Call(string tool, JObject input, bool readOnly, int? explicitPid = null)
        {
            int pid = explicitPid ?? _confirmedPid;
            if (pid < 0) throw new InvalidOperationException("请先确认接收端");
            if (pid == PsInstanceRegistry.SelfPid)
            {
                string raw;
                switch (tool)
                {
                    case "crossenv_inspect": raw = new CrossEnvInspectTool().Execute(input); break;
                    case "crossenv_transfer_check": raw = new CrossEnvTransferCheckTool().Execute(input); break;
                    case "crossenv_transfer_execute": raw = new CrossEnvTransferExecuteTool().Execute(input); break;
                    case "crossenv_transfer_status": raw = new CrossEnvTransferStatusTool().Execute(input); break;
                    case "crossenv_pick_request": raw = new CrossEnvPickRequestTool().Execute(input); break;
                    case "crossenv_pick_status": raw = new CrossEnvPickStatusTool().Execute(input); break;
                    default: throw new InvalidOperationException("未知传输工具");
                }
                if (raw.StartsWith("Error:", StringComparison.Ordinal)) throw new InvalidOperationException(raw);
                return JObject.Parse(raw);
            }
            var target = RemotePeer(pid);
            var response = await Task.Run(() => PsRpcClient.Invoke(target, tool, input, readOnly));
            if (!response.Ok)
            {
                var known = TransferResponses.ReadFailure(tool, response.Error, input);
                if (known != null) return known;
            }
            if (!response.Ok) throw new IOException("接收工程 " + target.Name + "（PID " + target.Pid + "）调用失败：" + (response.Error ?? "未返回结果")
                + ((response.Error ?? "").Contains("MissingMethodException") ? "；请确认两端 PS 均已重新加载新版 TxTools。" : ""));
            if (string.IsNullOrEmpty(response.Output) || response.Output.StartsWith("Error:", StringComparison.Ordinal))
                throw new IOException(response.Output ?? "接收端返回为空");
            return JObject.Parse(response.Output);
        }
        private async Task ConfirmReceiver()
        {
            int pid = SelectedPid();
            if (pid < 0) throw new InvalidOperationException("请选择接收工程");
            _status.Text = "正在读取接收工程，请稍候…";
            var description = await Call("crossenv_inspect", new JObject(), true, pid);
            if(pid!=SelectedPid()){_receiverPending=true;_contextTimer.Start();return;}
            if (_description != null && ReceiverScope(_confirmedPid,_description)!=ReceiverScope(pid,description))ClearReceiverBindings();
            if ((int?)description["protocol"] != 2) throw new InvalidOperationException("接收端插件版本不兼容，请在两端更新并重新加载插件（设备原型协议 v2）");
            if((bool?)description["native_library"]!=true)throw new InvalidOperationException("接收端尚不支持正式库引用检查，请更新并重新加载接收端插件。");
            if(_kind.SelectedIndex==0&&(bool?)description["library_subfolders"]!=true)throw new InvalidOperationException("接收端尚不支持显式子目录入库，请更新接收端插件后重试，避免模型误存到库根。");
            if(_kind.SelectedIndex==0&&((int?)description["library_save_version"]!=TransferRuntime.LibraryProtocol||(string)description["library_insert_mode"]!="guarded_appfunc_a"))throw new InvalidOperationException("接收端插入流程不兼容，请更新并重新加载两端插件（受保护的原路径插入 v6）。");
            _description = description; _confirmedPid = pid;
            _receiverPending=false;
            _receiver.Text = "接收工程：" + (string)description["study"];
            _storageNote.Text = _kind.SelectedIndex == 1 ? "焊点不需要复制模型文件。" : "接收库根：" + (string)description["systemRoot"];
            _status.Text = "";
            InvalidatePlan(); if (_plan != null && !_sourceDirty) SyncPlan();
            Log("接收工程：" + (string)description["study"] + "；SDK："+(string)description["sdk_runtime"]+"；Json.NET：" + (string)description["json_runtime"]
                +(_kind.SelectedIndex==0?"；模型流程：原路径插入 v6；中文兼容配置："+(string)description["unicode_insert_profile"]:""));
            if(_kind.SelectedIndex==0||_includeWeldParts.Checked)await LoadLibraryTree();
        }
        private void SyncPlan()
        {
            NeedPlan();
            if (_description == null || _confirmedPid != SelectedPid()) throw new InvalidOperationException("请选择接收工程并等待读取完成");
            string scope=ReceiverScope(_confirmedPid,_description);
            if(_plan.ReceiverBindingScope!=scope)
            { _plan.WeldBindings.Clear();_plan.ObjectMap.Clear();_plan.ReceiverBindingScope=scope; }
            _plan.TargetStudy = (string)_description["study"]; _plan.TargetDocument = (string)_description["document"];
            _plan.TargetStudyId = (string)_description["study_id"]; _plan.TargetRoot = (string)_description["systemRoot"];
            _plan.TargetParentId = null; // Scene hierarchy comes from the source; files use the folder tree.
            _plan.LibraryDirectories=new List<string>(_libraryDirectories);
            _plan.CopyModels = _copy.Checked; _plan.BindOnly = _bindOnly.Checked; _plan.Project = _project.Checked;
            _plan.AllowUnboundWelds=true;
            _plan.IncludeWeldParts=_plan.Kind=="weld"&&_includeWeldParts.Checked&&!string.IsNullOrWhiteSpace(_plan.WeldModelsFile);
            _plan.SingleDirectory = _singleDir.Text; _plan.DefaultDirectory = _fallback.Text;
            if (_sourceFrameInvalid||_frameInvalid) throw new InvalidOperationException("源端或接收端参考坐标无效，请重新选取或恢复世界坐标");
            _plan.SourceFrame=TxMath.WorldValues(_sourceOrigin.GetLocation()??new TxTransformation());
            _plan.TargetFrame=TxMath.WorldValues(_origin.GetLocation()??new TxTransformation());
            _plan.OriginX=_plan.OriginY=_plan.OriginZ=0;
            _plan.ObjectMap.Clear();
            if (_plan.Kind == "weld" && !string.IsNullOrEmpty(_plan.SourceStudyId) && _lastModelJob?.State == "succeeded" && _lastModel!=null && _lastModelPid == _confirmedPid
                && _lastModel.SourceStudy == _plan.SourceStudy && _lastModel.SourceDocument == _plan.SourceDocument
                && _lastModel.SourceStudyId == _plan.SourceStudyId && _lastModel.TargetStudy == _plan.TargetStudy
                && _lastModel.TargetDocument == _plan.TargetDocument && _lastModel.TargetStudyId == _plan.TargetStudyId
                && TransferPaths.Same(_lastModel.TargetRoot,_plan.TargetRoot))
                _plan.ObjectMap = new Dictionary<string, string>(_lastModelJob.ObjectMap);
        }
        private void BuildTypeRules()
        {
            if (_typeRules == null) return;
            _typeRules.SuspendLayout();
            foreach (Control child in _typeRules.Controls.Cast<Control>().ToList()) child.Dispose();
            _typeEditors.Clear();
            bool byType = _placement.SelectedIndex == 0;
            _basis.Visible = _typeRules.Visible = byType;
            _singleDir.Enabled = _placement.SelectedIndex == 1;
            _fallback.Enabled = byType||_placement.SelectedIndex==(int)TransferPlacement.Automatic;
            if (_plan != null && byType)
            {
                foreach (var category in _plan.Categories())
                {
                    var label = Caption(category.Value); _typeRules.Controls.Add(label);
                    var key = category.Key;
                    string value; _plan.TypeDirectories.TryGetValue(key, out value);
                    var editor = new TextBox { Font = FormUiKit.BaseFont, Text = value ?? "", Margin = new Padding(0, 0, 0, 5) };
                    editor.TextChanged += (s, e) => { if (_populating) return; _plan.TypeDirectories[key] = editor.Text; InvalidatePlan(); };
                    var row = new TableLayoutPanel { AutoSize = false, Height = 30, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
                    row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 74));
                    editor.Dock = DockStyle.Fill; row.Controls.Add(editor, 0, 0);
                    var browse = FormUiKit.MkBtn("选择…", FormUiKit.Theme.BtnSecondary, 70, 27);
                    browse.Click += (s, e) => { try { PickFolder(v => editor.Text = v); } catch (Exception ex) { ShowFailure(ex); } };
                    row.Controls.Add(browse, 1, 0); _typeEditors.Add(key, editor); _typeRules.Controls.Add(row);
                }
                if (_typeEditors.Count == 0) _typeRules.Controls.Add(Caption("此分类下没有已识别类型，请设置兜底目录或逐项指定。"));
            }
            _typeRules.ResumeLayout(); FitColumns();
        }
        private void ShowPackage()
        {
            var item = CurrentPackage;
            bool previous = _populating; _populating = true;
            try
            {
                _source.Text = item?.Source ?? "";
                _targetName.Text = item?.TargetName ?? ""; _conflict.SelectedIndex = item == null ? 0 : (int)item.Conflict;
            }
            finally { _populating = previous; }
        }
        private async Task RelocateSource()
        {
            var item = CurrentPackage ?? throw new InvalidOperationException("先选一个组件包");
            using (var dialog = new FolderBrowserDialog { Description = "选择这个模型对应的实际 cojt 目录" })
            {
                if (dialog.ShowDialog(DialogOwner) != DialogResult.OK) return;
                if (!dialog.SelectedPath.EndsWith(".cojt", StringComparison.OrdinalIgnoreCase)) throw new IOException("请选择 .cojt 目录本身");
                string selected=StoragePaths.Normalize(dialog.SelectedPath,TxApplication.SystemRootDirectory,null,null);
                var content=await Task.Run(()=>PackageFiles.Inspect(selected));
                item.Source = selected; item.SourceHash = content.Hash; item.Bytes=content.Bytes; item.TargetHash = null;
                ShowPackage(); InvalidatePlan();
                Log("已重新定位源模型: "+item.Source+"；原始结构关联保持不变，需重新检查。");
            }
        }
        private void PickFolder(Action<string> apply)
        {
            if (_description == null) throw new InvalidOperationException("请先选择接收工程并等待读取完成");
            string root = (string)_description["systemRoot"];
            using (var dialog = new FolderBrowserDialog { Description = "选择接收库内的保存目录", SelectedPath = root })
            {
                if (dialog.ShowDialog(DialogOwner) != DialogResult.OK) return;
                string path = TransferPaths.Destination(root, dialog.SelectedPath);
                apply(TransferPaths.Same(path, root) ? "." : CojtTransfer.RelativeToRoot(path, root));
            }
        }
        private async Task VerifyReuse()
        {
            SyncPlan(); var item = CurrentPackage ?? throw new InvalidOperationException("先选一个组件包");
            string target = _plan.Destination(item);
            var inspection = await Task.Run(() => PackageFiles.Inspect(target));
            string message = "源：" + item.OriginalSource + "\n目标：" + target + "\n目标指纹：" + inspection.Hash
                + "\n大小：" + inspection.Bytes + " 字节\n\n请在接收工程核对该模型的几何、坐标与类型。确认复用此版本？";
            if (MessageBox.Show(DialogOwner, message, "人工核验复用", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            _populating = true; _conflict.SelectedIndex = 2; _populating = false;
            item.Conflict = TransferConflict.ReuseVerified; item.TargetHash = inspection.Hash; InvalidatePlan();
            Log("已记录目标内容指纹；执行前会再次校验：" + target);
        }
        private void SaveRules()
        {
            SyncPlan();
            using (var dialog = new SaveFileDialog { Filter = "目录分配方案|*.json", FileName = "CrossEnv-folders.json" })
                if (dialog.ShowDialog(DialogOwner) == DialogResult.OK)
                    TransferFile.Write(dialog.FileName, new JObject { ["version"] = 1, ["root"] = _plan.TargetRoot,
                        ["basis"] = _plan.Classification, ["placement"] = (int)_plan.Placement,
                        ["single"] = _singleDir.Text, ["fallback"] = _fallback.Text, ["types"] = JObject.FromObject(_plan.TypeDirectories) }.ToHostJson(Formatting.Indented));
        }
        private void LoadRules()
        {
            SyncPlan();
            using (var dialog = new OpenFileDialog { Filter = "目录分配方案|*.json" })
            {
                if (dialog.ShowDialog(DialogOwner) != DialogResult.OK) return;
                var data = JObject.Parse(File.ReadAllText(dialog.FileName, Encoding.UTF8));
                if ((int?)data["version"] != 1 || !TransferPaths.Same((string)data["root"], _plan.TargetRoot))
                    throw new InvalidDataException("目录方案属于不同库根，请为当前接收库重新指定");
                string basis = (string)data["basis"];
                int basisIndex = Array.IndexOf(new[] { "sdk", "planning", "prototype", "family" }, basis);
                int placement = (int?)data["placement"] ?? -1;
                if (basisIndex < 0 || placement < 0 || placement > (int)TransferPlacement.Automatic) throw new InvalidDataException("目录方案类型无效");
                _plan.TypeDirectories = data["types"].ToObject<Dictionary<string, string>>();
                _plan.Classification = basis; _plan.Placement = (TransferPlacement)placement;
                _populating = true;
                _basis.SelectedIndex = basisIndex; _placement.SelectedIndex = placement;
                _singleDir.Text = (string)data["single"] ?? ""; _fallback.Text = (string)data["fallback"] ?? "";
                _plan.SingleDirectory=_singleDir.Text;_plan.DefaultDirectory=_fallback.Text;
                _populating = false; BuildTypeRules(); UpdateDestination(); InvalidatePlan();
            }
        }
        private void PreviewPlan()
        {
            SyncPlan();
            var text = new StringBuilder();
            text.AppendLine(_plan.SourceStudy + " → " + _plan.TargetStudy);
            text.AppendLine("库根：" + _plan.TargetRoot + "\n重建到文档根，保留源层级和每个实例的世界位姿。");
            if(_plan.Kind=="model")
            {
                var table=TransferTable.Read(_plan.DataFile);
                foreach(var row in table.Rows.Where(r=>!string.IsNullOrWhiteSpace(table.Value(r,"TargetName"))))
                    text.AppendLine("同名根独立重建："+table.Value(row,"Name")+" → "+table.Value(row,"TargetName"));
            }
            foreach (var item in _plan.Packages)
            {
                text.AppendLine("\n" + item.Name + " · " + item.Instances + " 实例 · " + item.Conflict);
                text.AppendLine("原始：" + item.OriginalSource + "\n实际：" + item.Source);
                if(item.Prototype!=null)text.AppendLine("源设备原型："+item.Prototype.Type+" / "+item.Prototype.Family
                    +"；原生类型："+item.Prototype.RuntimeType+(string.IsNullOrEmpty(item.Prototype.Error)?"":"\n原型问题："+item.Prototype.Error));
                try { text.AppendLine("目标：" + (item.Conflict == TransferConflict.Skip ? "跳过" : _plan.Destination(item))); }
                catch (Exception ex) { text.AppendLine("待处理：" + ex.Message); }
            }
            using (var dialog = new Form { Text = "逐项传输计划", Size = new Size(600, 540), StartPosition = FormStartPosition.CenterParent })
            {
                dialog.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
                    WordWrap = false, Text = text.ToString(), Font = FormUiKit.BaseFont }); dialog.ShowDialog(DialogOwner);
            }
        }
        private async Task Preflight()
        {
            if (_finishedJob) throw new InvalidOperationException("当前传输已完成，请重新选取对象以开始新的传输");
            SyncPlan(); _plan.PreparedStamp = null; _issues.Items.Clear(); _bindingIssues.Clear();
            if((bool?)_description["frame_alignment"]!=true||_plan.Kind=="weld"&&(bool?)_description["optional_weld_binding"]!=true
                ||_plan.IncludeWeldParts&&(bool?)_description["weld_with_models"]!=true)
                throw new InvalidOperationException("接收端尚不支持双端坐标定位、可选焊点绑定或携带零件传输，请更新并重新加载接收端插件。");
            bool namedRoots=UsesNamedRoots();
            if(namedRoots&&(bool?)_description["named_roots"]!=true)throw new InvalidOperationException("同名导出根需要新版接收端支持独立目标名称，请更新接收端插件。");
            _status.Text = "正在检查文件与目标场景…";
            var fileReport = await Task.Run(() => _plan.InspectFiles());
            foreach (string error in fileReport.Errors) _issues.Items.Add(ExplainCheckIssue("文件 / 存放分配",error));
            JObject result;
            try{result = await Call("crossenv_transfer_check", new JObject { ["plan"] = JObject.FromObject(_plan) }, true);}
            catch(Exception ex){result=new JObject { ["ok"]=false,["errors"]=new JArray("接收端检查未完成："+ex.Message) };}
            if(_plan.Kind=="model"&&(bool?)result["ok"]==true&&(bool?)result["native_library"]!=true)
                throw new InvalidOperationException("接收端预检缺少原生入库能力，请更新接收端插件后重新检查。");
            if(_plan.Kind=="model"&&(bool?)result["ok"]==true&&(bool?)result["library_subfolders"]!=true)
                throw new InvalidOperationException("接收端预检缺少显式子目录入库能力，请更新接收端插件。");
            if(_plan.Kind=="model"&&(bool?)result["ok"]==true&&((int?)result["library_save_version"]!=TransferRuntime.LibraryProtocol||(string)result["library_insert_mode"]!="guarded_appfunc_a"))
                throw new InvalidOperationException("接收端插入流程不兼容，请更新接收端插件（受保护的原路径插入 v6）。");
            foreach (var issue in result["bindings"] as JArray ?? new JArray())
            {
                var binding=new BindingIssue { Key=(string)issue["key"],Message=(string)issue["message"],ReceiverScope=ReceiverScope(_confirmedPid,_description),
                    DisplayText=ExplainCheckIssue("接收工程 / 重建 · "+(string)issue["key"],(string)issue["message"]) };
                _bindingIssues.Add(binding);_issues.Items.Add(binding);
            }
            var bindingMessages=new HashSet<string>(_bindingIssues.Select(i=>i.Message),StringComparer.Ordinal);
            foreach (var error in result["errors"] as JArray ?? new JArray())
                if(!bindingMessages.Contains((string)error))_issues.Items.Add(ExplainCheckIssue("接收工程 / 重建",(string)error));
            _summary.Text = _plan.TargetStudy + " · " + fileReport;
            if(namedRoots)_summary.Text+="\n同名根按源父级及身份后缀分别命名，详见传输明细。";
            if (fileReport.Ok && (bool?)result["ok"] == true)
            {
                _issues.Visible = false;_issueDetail.Visible=false;
                _plan.TargetSceneStamp = (string)result["scene_stamp"]; _plan.PreparedStamp = _plan.Stamp();
                _plan.Save(Path.Combine(_session, _plan.JobId + ".json")); _status.Text = "预检通过 · 可以执行传输";
            }
            else
            {
                _summary.Text="检查未通过 · "+_issues.Items.Count+" 个问题\n尚未复制或修改场景。";
                _status.Text="";ShowProblems();
                foreach(var issue in _issues.Items)Log(Convert.ToString(issue));
            }
            Log((_issues.Items.Count>0 ? _summary.Text : _status.Text) + "；" + fileReport);
        }
        private bool UsesNamedRoots()
        {
            if(_plan?.Kind!="model")return false;
            var table=TransferTable.Read(_plan.DataFile);
            return table.Rows.Any(r=>!string.IsNullOrWhiteSpace(table.Value(r,"TargetName")));
        }
        private static string ExplainCheckIssue(string area,string error)
        {
            try{error=Uri.UnescapeDataString(error);}catch(UriFormatException){ }
            string action=error.Contains("中文路径兼容")||error.Contains("AppFunc")?(error.Contains("恢复")?"接收端兼容处理未能恢复，请重启接收端 PS 后重新检查。":"接收端实际插入指令或模块布局与当前兼容实现不匹配，请保留上方具体原因；产品版本字符串不作为拦截条件。"):
                error.Contains("没有可读的正式库引用")||error.Contains("指定的正式库文件夹")||error.Contains("组件仍在建模")?"在接收工程核对已有实例；需改存放位置时使用另存为，或核对并撤销失败导入后重新传输。对象名与 cojt 文件名无需一致。":
                error.Contains("父容器")||error.Contains("同名实例")?"在接收工程核对同名对象和源端层级；调整传输范围，或核对并撤销错误实例后重新检查。库文件夹分配只决定文件位置。":
                error.Contains("Planning")||error.IndexOf("planning type",StringComparison.OrdinalIgnoreCase)>=0?"核对具体报错阶段及原型 Type / Family；此异常也可能由 Planning 元数据未加载引起，不能仅据此判定模型未定义。":
                error.Contains("原型")?"按上方原因核对原型 Type / Family 和写入回读结果；源记录缺少字段时重新捕获，不把完整原型强制替换成其他类型。":
                error.Contains("绑定")||error.Contains("零件")&&area.Contains("重建")?"选择对应焊点问题，点击“为选中焊点指定零件”，在接收工程重新绑定。":
                error.Contains("同名")||error.Contains("分配")||error.Contains("目录")||error.Contains("组件包")?"在第二栏定位该文件并拖到接收库目录；同名内容不同时使用“高级规则”指定新名称或核验复用。":
                error.Contains("建模")?"切换到接收工程，结束或取消正在进行的建模，再重新检查。":
                error.Contains("源")||error.Contains("数据文件")?"重新选取源对象并检查；导入文件时重新打开数据文件，源 cojt 缺失时在高级规则中重新定位。":
                "核对接收工程、对象和文件路径；详细日志中保留完整检查信息。处理后重新检查。";
            return "【"+area+"】"+error+"\n处理："+action;
        }
        private async Task RequestPick(string purpose, string row)
        {
            if (purpose == "binding") { EnsureCaptured(); SyncPlan(); }
            if (_description == null) await ConfirmReceiver();
            string id = Guid.NewGuid().ToString("N");
            string scope = ReceiverScope(_confirmedPid,_description);
            await Call("crossenv_pick_request", new JObject { ["id"] = id, ["purpose"] = purpose, ["row_key"] = row,
                ["message"] = purpose == "frame" ? "在此接收工程中使用原生坐标框选取目标参考坐标，再确认返回。" : "请为该焊点多选绑定零件：" + row }, false);
            _pickId=id; _pickPurpose=purpose; _pickRow=row; _pickReceiverScope=scope;
            _pickTimer.Start(); _status.Text = "请在接收工程中确认选取，结果会自动带回。"; Log(_status.Text);
        }
        private async Task ReadPick()
        {
            if (_pickId == null) throw new InvalidOperationException("尚未发起接收端拾取");
            string id=_pickId,scope=_pickReceiverScope;
            var result = await Call("crossenv_pick_status", new JObject { ["id"] = id }, true);
            if(_pickId!=id||scope!=ReceiverScope(_confirmedPid,_description))return;
            if ((string)result["State"] == "waiting") return;
            if ((string)result["State"] != "confirmed") { _pickTimer.Stop(); _pickId = null; _status.Text = "选取已取消，可继续修改设置。"; return; }
            if ((string)result["Study"] != (string)_description["study"] || (string)result["Document"] != (string)_description["document"] || (string)result["StudyId"] != (string)_description["study_id"])
            {ClearReceiverBindings();_receiverPending=true;throw new InvalidOperationException("选取结果所属工程已变化，请重新确认接收端");}
            if(_pickPurpose=="frame")
            {
                _origin.SetLocation(TxMath.FromWorldValues(result["WorldFrame"].ToObject<double[]>()));_frameInvalid=false;
            }
            else _plan.WeldBindings[_pickRow] = result["Parts"].ToObject<List<string>>();
            _pickTimer.Stop(); _pickId = null; InvalidatePlan(); _status.Text = "选取已更新，可点击“检查重建条件”。"; Log(_status.Text);
        }
        private async Task BindIssue()
        {
            if (_plan?.Kind != "weld") throw new InvalidOperationException("此操作仅用于焊点绑定问题");
            var issue = _issues.SelectedItem as BindingIssue;
            if (issue == null||!_bindingIssues.Contains(issue)||string.IsNullOrEmpty(issue.Key)) throw new InvalidOperationException("请选中一个焊点绑定问题，再请求接收端原生多选");
            if(issue.ReceiverScope!=ReceiverScope(_confirmedPid,_description))throw new InvalidOperationException("焊点问题属于旧接收工程，请重新检查后指定零件");
            await RequestPick("binding", issue.Key);
        }
        private async Task ExecutePlan()
        {
            SyncPlan();
            if (string.IsNullOrEmpty(_plan.PreparedStamp) || _plan.PreparedStamp != _plan.Stamp())
                throw new InvalidOperationException("传输配置已变化，请重新点击“检查重建条件”");
            var receiver = await Call("crossenv_transfer_check", new JObject { ["plan"] = JObject.FromObject(_plan) }, true);
            if ((bool?)receiver["ok"] != true || (string)receiver["scene_stamp"] != _plan.TargetSceneStamp
                ||(bool?)receiver["frame_alignment"]!=true
                ||_plan.Kind=="weld"&&(bool?)receiver["optional_weld_binding"]!=true
                ||_plan.IncludeWeldParts&&(bool?)receiver["weld_with_models"]!=true
                ||_plan.Kind=="model"&&(bool?)receiver["native_library"]!=true
                ||_plan.Kind=="model"&&(bool?)receiver["library_subfolders"]!=true
                ||_plan.Kind=="model"&&((int?)receiver["library_save_version"]!=TransferRuntime.LibraryProtocol||(string)receiver["library_insert_mode"]!="guarded_appfunc_a")
                ||UsesNamedRoots()&&(bool?)receiver["named_roots"]!=true)
            { InvalidatePlan(); throw new InvalidOperationException("接收工程条件已变化，请重新检查；尚未复制或修改场景"); }
            _status.Text = "正在复制文件 · 可以取消";
            _cancellation = new CancellationTokenSource(); _cancel.Enabled = true; UpdateActions();
            CopyResult batch;
            try { batch = await Task.Run(() => _plan.Copy(Log, _cancellation.Token)); }
            catch (TransferCopyException ex)
            {
                ex.PartialResult.TargetPids.Add(_confirmedPid); ex.PartialResult.Merge(_copies); _copies = ex.PartialResult;
                if (ex.InnerException is OperationCanceledException) throw new OperationCanceledException();
                throw;
            }
            batch.Merge(_copies); _copies = batch; _cancel.Enabled = false;
            _copies.TargetPids.Add(_confirmedPid);
            _cancellation.Token.ThrowIfCancellationRequested();
            // 请求发出前持久化上下文。任何断连都按可能已经修改场景处理。
            _plan.Save(Path.Combine(_session, _plan.JobId + ".json"));
            TransferFile.Write(PendingFile, new JObject { ["pid"] = _confirmedPid, ["plan"] = JObject.FromObject(_plan) }.ToHostJson(Formatting.Indented));
            _copies.SceneMayReferenceFiles = true; _copies.TargetPids.Add(_confirmedPid);
            _submittedFingerprint=_plan.PreparedStamp??_plan.Stamp();
            _uncertain = true; _status.Text = "接收端正在执行 · 等待结果";
            try
            {
                var response = await Call("crossenv_transfer_execute", new JObject { ["plan"] = JObject.FromObject(_plan) }, false);
                if (response["log"] != null) Log((string)response["log"]);
                var job = response["job"]?.ToObject<TransferJob>();
                if (job == null)
                {
                    if ((bool?)response["not_started"] == true)
                    {
                        _uncertain = false;
                        if (File.Exists(PendingFile)) File.Delete(PendingFile);
                        InvalidatePlan();
                    }
                    throw new InvalidOperationException((string)response["error"] ?? "目标未返回任务结果，请查询状态");
                }
                AcceptJob(job);
            }
            catch { if (_uncertain) Log("执行结果尚未确认。请查询任务状态，不要重复重建。任务：" + _plan.JobId); throw; }
        }
        private void AcceptJob(TransferJob job)
        {
            if (job.Id != _plan.JobId || job.Fingerprint != (_submittedFingerprint??_plan.PreparedStamp??_plan.Stamp())) throw new InvalidDataException("接收任务标识或计划指纹不一致");
            _submittedFingerprint=job.Fingerprint;
            Log("任务 " + job.Id + "：" + job.State + "\n" + job.Message);
            _status.Text = "接收端任务：" + job.State;
            if (job.State != "succeeded" && job.State != "failed") return;
            _uncertain = false;
            _finishedJob = true;
            _jobSucceeded = job.State == "succeeded";
            if (File.Exists(PendingFile)) File.Delete(PendingFile);
            if(job.SceneReloaded)
            {
                _plan.TargetStudy=job.Study;_plan.TargetStudyId=job.StudyId;_plan.TargetDocument=job.Document;_plan.TargetRoot=job.TargetRoot;
                _plan.TargetParentId=null;
                if(_description!=null){_description["study"]=job.Study;_description["study_id"]=job.StudyId;_description["document"]=job.Document;_description["systemRoot"]=job.TargetRoot;}
            }
            else if(job.ReloadAttempted){_description=null;}
            if ((_plan.Kind == "model"||_plan.IncludeWeldParts) && job.State == "succeeded")
            { _lastModel = JsonConvert.DeserializeObject<TransferPlan>(JsonConvert.SerializeObject(_plan)); _lastModelJob = job; _lastModelPid = _confirmedPid; }
            // 完成的任务保留 ID；再次点击执行只能查询同一结果，不会再次修改场景。
            _plan.PreparedStamp = null;
            _status.Text = job.State == "succeeded" ? job.SceneReloaded?"已完成 · 接收工程已保存修补并重载，原 PSZ 已备份":"已完成 · 请保存接收工程；场景撤销在接收端操作"
                :job.ReloadAttempted?"未完成 · 已尝试重载，请核对当前工程与备份文件":"部分失败 · 查看日志，在接收端核对或 Ctrl+Z 后重新规划";
            if (!_jobSucceeded)
            {
                _issues.Items.Clear(); _issues.Items.Add(job.Message);
                _summary.Text = job.ReloadAttempted?"接收端重载未完整完成，请核对当前工程和 PSZ 备份后重新规划。":"接收端未完整完成，请先核对或撤销已有变更，再调整设置。";
                ShowProblems();
            }
            else _summary.Text=job.SceneReloaded?"已完成 · 接收工程已保存、修补并重载。原 PSZ 备份："+job.PszBackup:"已完成 · 接收端场景已更新。";
        }
        private async Task QueryJob()
        {
            if (_plan == null || _confirmedPid < 0) throw new InvalidOperationException("没有可查询的任务");
            var result = await Call("crossenv_transfer_status", new JObject { ["job_id"] = _plan.JobId }, true);
            if ((bool?)result["ok"] != true) throw new IOException((string)result["error"]);
            var job = result["job"]?.Type == JTokenType.Object ? result["job"].ToObject<TransferJob>() : null;
            if (job == null) { Log("接收端尚无此任务记录，执行结果仍不能确认；保留文件，不重发重建。"); return; }
            AcceptJob(job);
        }
        private async Task CleanupFiles()
        {
            if (_copies == null || _copies.CopiedDirs.Count == 0) { Log("没有可清理的复制记录。"); return; }
            if (_copies.SceneMayReferenceFiles) { _copies.Undo(Log); return; }
            var directories = _copies.CopiedDirs.ToList();
            var live = PsInstanceRegistry.Live().Where(p => !p.IsSelf).ToList();
            if (_copies.TargetPids.Any(pid => pid != PsInstanceRegistry.SelfPid && live.All(p => p.Pid != pid)))
                throw new InvalidOperationException("复制目标实例已离线，无法核验工程引用，保留文件");
            var safe = directories.ToDictionary(d => d, d => !ComponentIO.IsPathReferencedByScene(d), StringComparer.OrdinalIgnoreCase);
            foreach (var peer in live)
            {
                var info = await Call("crossenv_inspect", new JObject { ["directories"] = JArray.FromObject(directories) }, true, peer.Pid);
                foreach (string directory in directories) safe[directory] &= (bool?)info["references"]?[directory] == false;
            }
            if (!new HashSet<int>(live.Select(p => p.Pid)).SetEquals(PsInstanceRegistry.Live().Where(p => !p.IsSelf).Select(p => p.Pid)))
                throw new InvalidOperationException("核验期间实例列表变化，请重新清理");
            var copy = _copies; await Task.Run(() => copy.Undo(Log, d => safe.ContainsKey(d) && safe[d]));
        }
        private void RestorePending()
        {
            if (_uncertain || !File.Exists(PendingFile)) return;
            try
            {
                var value = JObject.Parse(File.ReadAllText(PendingFile, Encoding.UTF8));
                var plan = value["plan"].ToObject<TransferPlan>();
                string id = plan.JobId, stamp = plan.PreparedStamp, scene = plan.TargetSceneStamp;
                SetPlan(plan,true); plan.JobId = id; plan.PreparedStamp = stamp; plan.TargetSceneStamp = scene;
                _sourceFromFile = true;
                _confirmedPid = (int)value["pid"];
                _description = new JObject { ["study"] = plan.TargetStudy, ["document"] = plan.TargetDocument,
                    ["study_id"] = plan.TargetStudyId, ["systemRoot"] = plan.TargetRoot, ["protocol"] = plan.Version };
                _uncertain = true; _status.Text = "存在待确认任务 · 请查询执行状态"; UpdateActions();
                Log(_status.Text + "；任务：" + id);
            }
            catch (Exception ex)
            {
                _uncertain = true; UpdateActions();
                Log("未能读取待确认任务，已禁止重建。请核对记录：" + PendingFile + "\\n" + ex.Message);
            }
        }
    }
}
