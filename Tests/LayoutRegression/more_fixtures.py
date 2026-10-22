# Executed by generate.py; each fixture uses current production builders.
def theme_colors(*names):
    global common
    for name in names:
        if not re.search(r'\b' + name + r'\s*=', block(common, r'^static class Theme \{')):
            common = common.replace('\nstatic class Theme {', '\nstatic class Theme { public static Color ' + name + '=Color.Gray;', 1)

def fixture(name, path, builders, boundary, constructor):
    global common
    text = source(path)
    design = re.search(r'(?:_designSize|_design|DesignSize) = new Size\((\d+), (\d+)\)', text)
    if design:
        constructor = re.sub(r'ClientSize=new Size\(\d+,\d+\)', 'ClientSize=new Size('+str(int(design[1])-16)+','+str(int(design[2])-38)+')', constructor)
    ui = methods(text, builders)
    theme_colors(*re.findall(r'(?<!FormUiKit\.)\bTheme\.(\w+)(?!\w|\s*\()', ui))
    common += 'class '+name+' : Form {\n'+fields(text)+'\n'+ui+'\n'+boundary+'\npublic '+name+'(){Font=FormUiKit.BaseFont;AutoScaleMode=AutoScaleMode.None;'+constructor+'}\n}\n'

fixture('LineFixture', 'LineToSolid/LineToSolidForm.cs',
 'BuildToolStrip BuildBody BuildLeftColumn BuildRightColumn FitSectionLayout UpdateBuildButtonText MkParamTable AddRow AddSpanRow MkNud BuildRectTable BuildCylTable UpdateCylEnabledState BuildLogPanel',
 '''TxToolStrip _toolStrip;TxObjGridCtrl _featureGrid;bool _dpiApplied,_fittingSection;
 void RefreshSegmentInfo(){} void OnBuild(){} void ToggleLog(){}
 ''', 'ClientSize=new Size(704,622);MinimumSize=new Size(680,420);BuildToolStrip();BuildBody();BuildLogPanel();Shown+=(s,e)=>{_dpiApplied=true;FitSectionLayout();};')

fixture('BaseFixture', 'RobotBaseChecker/RobotBaseCheckerForm.cs', 'BuildUi',
 '''void RunCheck(){} void SyncAll(){} void ExportCsv(){}void ShowSelectedDetail(){}void FocusRobot(int row){}void FitColumnsToGrid(){}
 ''','ClientSize=new Size(984,582);BuildUi();')

fixture('MatrixFixture', 'WeldPointMatrix/WeldPointMatrixForm.cs', 'BuildUi Card AddButton',
 '''class Picker{public void Clear(){}} Picker _picker=new Picker();class SpotData{public string Name;public PointType Kind;}enum PointType{WeldPoint}
 void RefreshMatrix(bool b){}void InsertRow(){}void AddPoint(bool b){}
 void TableCellClick(object s,DataGridViewCellEventArgs e){}void TableMouseDown(object s,MouseEventArgs e){}void TableMouseMove(object s,MouseEventArgs e){}
 void TableDragOver(object s,DragEventArgs e){}void TableDragDrop(object s,DragEventArgs e){}
 ''','ClientSize=new Size(1104,662);BuildUi();')

# Full CATIA window (the earlier CatiaFixture verifies the tree editor alone).
fixture('CatiaFullFixture','CatiaPartTree/CatiaPartTreeForm.cs',
 'BuildBody BuildBottomBar BuildTreeCard AddEditButton BuildRightColumn BuildTargetCard BuildActionCard BuildLogCard',
 '''object _model=new object();void RefreshTree(){}void CaptureExpansion(){}void SetAllChecked(bool b){}void ShowHelp(){}
 void OnTreeAfterCheck(object s,TreeViewEventArgs e){}void OnTreeAfterLabelEdit(object s,NodeLabelEditEventArgs e){}
 void OnReadTree(){}void OnAddChild(){}void OnAddSibling(){}void OnRename(){}void OnDelete(){}void OnMoveUp(){}void OnMoveDown(){}void OnIndent(){}void OnOutdent(){}
 void OnBuildTree(){}void OnClassify(){}enum LogLevel{Info}void Log(string s,LogLevel l){}class Service{public void ClearMapping(){}}Service _svc=new Service();
 ''','ClientSize=new Size(1064,682);BuildBody();BuildBottomBar();')

theme = source('ThemeTuner/ThemeTunerForm.cs')
slots = block(theme, r'        private static readonly string\[\]\[\] Slots') + ';'
fixture('ThemeFixture','ThemeTuner/ThemeTunerForm.cs',
 'BuildBody BuildPresetCard BuildColorCard NewSlotColumn MakeSlotRow BuildPreviewCard NewPreviewButton BuildStatusCard BuildBottomBar',
 slots + '''Dictionary<string,FlatColorButton> _swatches=new Dictionary<string,FlatColorButton>();
 void OnPresetChanged(){}void PickColor(string s,FlatColorButton b){}void ApplyAndSave(){}void ToggleFlatStyle(){}void ResetToDefault(){}void SyncColorUiState(){}
 string FlatText(){return "扁平风格：关";}Color G(string key){return Color.SteelBlue;}static string Hex(Color c){return "#4682B4";}
 ''','ClientSize=new Size(664,562);BuildBody();BuildBottomBar();')

fixture('MechFixture','MechArena/MechArenaForm.cs',
 'BuildUi BuildHeader BuildBody BuildRobotListPanel BuildBottom MonoFont',
 '''void StartGame(){}void StopGame(){}void RestartGame(){}
 ''','ClientSize=new Size(584,482);BuildUi();')

fixture('FenceFullFixture','AutoFance/FenceBuilderForm.cs',
 'BuildUI BuildMeshCard BuildPostCard BuildOptionCard NewCard NewCardFlow AddParamRow AddColorRow NewFlatButton',
 '''TxObjGridCtrl _baselineGrid;bool _logVisible=true;
 void OnAddFromSelection(object s,EventArgs e){}void OnClearList(object s,EventArgs e){}void OnGenerate(object s,EventArgs e){}void OnUndoLast(object s,EventArgs e){}
 ''','ClientSize=new Size(1084,682);BuildUI();')

fixture('PathFixture','AutoPath/AutoPathPlannerForm.cs',
 'BuildUI MakeCard MakeBtn AddRow AddCheck AddNote',
 '''readonly ToolTip _tip=new ToolTip();bool _stopRequested;
 void AppendOpsSelection(){}void ClearGrid(TxObjGridCtrl g){}void OnAutoCreateCollisionSet(object s,EventArgs e){}void ResetParamsToDefault(){}void OnRun(object s,EventArgs e){}
 enum LogLevel{Warn}void Log(string s,LogLevel l){}TxObjGridCtrl _gridOps;
 ''','ClientSize=new Size(744,722);BuildUI();')

fixture('GrouperFixture','WeldSpotGrouper/Ui/GrouperForm.cs','BuildUI',
 '''TxObjGridCtrl _scopeGrid;void DoScan(){}void DoApply(){}void DoClear(){}void ToggleLog(){}void RefreshScopeLabel(){}void FlushLog(){}
 ''','ClientSize=new Size(388,622);BuildUI();')

fixture('ColorFixture','ExportByColor/ExportByColorForm.cs',
 'BuildUi BuildBody BuildResourceCard BuildParamCard BuildBottom',
 '''TxObjGridCtrl _objGrid;TxObjEditBoxCtrl _objOrigin;void OnGridObjectInserted(object s,EventArgs e){}void OnGridRowDeleted(object s,EventArgs e){}
 void OnOriginPicked(object s,EventArgs e){}void ClearGrid(){}void EnumerateVisible(){}void RunExport(){}
 ''','ClientSize=new Size(944,702);BuildUi();')
color_source=source('ExportByColor/ExportByColorForm.cs')
mode_block=block(color_source, r'        private sealed class ExportMode')
backend_values=sorted(set(re.findall(r'CgrBackend\.(\w+)', mode_block+methods(color_source,'BuildParamCard'))))
common=common.replace('class ColorFixture : Form {','class ColorFixture : Form {\nenum CgrBackend{'+','.join(backend_values)+'}\n'+mode_block)

fixture('CrossFixture','CrossEnvIO/CrossEnvIOForm.cs',
 'BuildUi BuildTransferTab ApplyMode BuildWeldTab BuildLogBox MkBrowseBtn GetText',
 '''static string DefaultDir=System.IO.Path.GetTempPath();bool _partMode;TxObjGridCtrl _gridRoot,_gridWeldRoot,_gridWeldSel;
 TxFrameEditBoxCtrl _frameOrigin;double _originX,_originY,_originZ;
 class TxFrameEditBoxCtrl:TextBox{public bool ListenToPick{get;set;}public TxPickLevel PickLevel{get;set;}public event EventHandler ValidFrameSet{add{}remove{}}public event EventHandler InvalidFrameSet{add{}remove{}}public void SetLocation(TxTransformation t){}}
 enum TxPickLevel{Entity}class TxTransformation{}class TxObjectList:List<object>{}
 void OnOriginFrameSet(object s,EventArgs e){}void DetectLibraryRoots(){}void RefreshPeerList(){}void OnPeerChanged(){}
 void RunExport(){}void RunCopyCojts(){}void RunRebuild(){}void RunRebuildRemote(){}void RunPushToPeer(){}void RunUndoCopy(){}
 void RunExportWeld(){}void RunRebuildWeld(){}void RunPushWeldToPeer(){}void Log(string s){}static string Browse(string kind,string initial){return null;}
 ''','ClientSize=new Size(744,602);BuildUi();')

recorder = source('AutoRecorder/AutoRecorderForm.cs')
recorder_constants = '\n'.join(re.findall(r'^        private const (?:int|float) [^\n;]*;',recorder,re.M))
speedup = block(recorder, r'        private class SpeedupItem')
fixture('RecorderFixture','AutoRecorder/AutoRecorderForm.cs',
 'BuildUi AddRootRow BuildParamsTable AddParamRow ParamLabel InlineLabel MkInlineFlow NewGroupBox NewButton BuildKeyframeCard',
 recorder_constants + speedup + '''TxObjGridCtrl _grid;
 void UpdateFormHeight(){}void OnOperationPicked(){}void RefreshKfOpSelector(){}void RefreshKfButtonState(){}void RefreshKfPanelContent(){}
 void OnKfOpSelectorChanged(object s,EventArgs e){}void OnKfSet(object s,EventArgs e){}void OnKfPreview(object s,EventArgs e){}void OnKfClear(object s,EventArgs e){}
 void OnObjectsGridSelectionChanged(object s,EventArgs e){}
 static bool HasFfmpegAvailable(){return false;}
 ''','ClientSize=new Size(FORM_W-16,FORM_H_FALLBACK-38);BuildUi();')

annotator_main=source('WeldAnnotator/WeldAnnotatorForm.cs').replace('WF.','').replace('D.','')
annotator_ui=source('WeldAnnotator/WeldAnnotatorForm.UI.cs').replace('WF.','').replace('D.','')
annotator_builders=methods(annotator_ui,'BuildCardPanel MakeRailCard MkRailBtn MkRailBtnWide FillRailCardGrid UpdateLabelCardEnabled BuildStatusBar BuildLogPanel')
theme_colors(*re.findall(r'\bTheme\.(\w+)(?!\w|\s*\()',annotator_builders))
common += 'class AnnotatorFixture:Form{'+fields(annotator_main)+annotator_builders+'''
TxObjGridCtrl _opGrid;bool _logVisible;object _selOpObj;ToolStripProgressBar _progress;
enum LabelNamingMode{OriginalName,Sequential,''' + ','.join(sorted(set(re.findall(r'LabelNamingMode\.(\w+)',annotator_builders)) - {'OriginalName','Sequential'})) + '''}void ToggleLog(ToolStripButton b){}
void OnOpInserted(object s,EventArgs e){}void OnOpDeleted(object s,EventArgs e){}
void BtnSnap_Click(object s,EventArgs e){}void BtnRestore_Click(object s,EventArgs e){}void BtnShowOnly_Click(object s,EventArgs e){}
void BtnExport_Click(object s,EventArgs e){}void BtnClear_Click(object s,EventArgs e){}void BtnStyle_Click(object s,EventArgs e){}
void RefreshGrid(){}void ApplyAutoThicknessIfEnabled(){}void UpdateDisplayControls(){}
public AnnotatorFixture(){Font=FormUiKit.BaseFont;AutoScaleMode=AutoScaleMode.None;ClientSize=new Size(984,722);
Controls.Add(new DataGridView{Dock=DockStyle.Fill});BuildCardPanel();BuildLogPanel();BuildStatusBar();}
}
'''

wizard=source('WeldGunDefiner/UI/WeldGunWizardForm.cs')
wizard_ui=methods(wizard,'BuildUi BuildStepIndicator BuildButtonBar BuildStep0 BuildStep1 BuildStep2 BuildStep3 FitStepHeight UpdateStepUi MkCard MkFixedCard MkStack MkGrid')
# Replace scene picking callbacks, preserving all controls, rows and UI layout callbacks.
masked=re.sub(r'//[^\n]*|/\*[\s\S]*?\*/|"(?:\\.|[^"\\])*"',lambda m:' '*len(m[0]),wizard_ui)
native_handlers=list(re.finditer(r'\.(?:ObjectInserted|RowDeleted|Picked|ValidFrameSet|InvalidFrameSet|Leave)\s*\+=\s*\([^)]*\)\s*=>\s*\{',masked))
for match in reversed(native_handlers):
    opening=match.end()-1
    depth=1;end=opening+1
    while depth:
        depth+=(masked[end]=='{')-(masked[end]=='}');end+=1
    wizard_ui=wizard_ui[:opening+1]+wizard_ui[end-1:]
theme_colors(*re.findall(r'\bTheme\.(\w+)(?!\w|\s*\()',wizard_ui))
common += 'class WizardFixture:Form{'+fields(wizard)+wizard_ui+'''
Panel[] _stepPanels;int _currentStep;bool _dpiApplied,_gunFilterReliable,_suppressOpenGapEvent,_openGapUserEdited;
TxObjGridCtrl _gridO,_gridA,_gridC,_gridStaticTip,_gridMovingTip,_gridFixedLink,_gridInputLink,_gridCouplerLink,_gridOutputLink,_gridLnk2,_gridTargetDevice;
TxFrameEditBoxCtrl _tcpEditBox;TxTransformation _tcpPickedLocation;
List<Tuple<TxObjGridCtrl,List<ITxObject>,string,TxColor>> _linkGrids=new List<Tuple<TxObjGridCtrl,List<ITxObject>,string,TxColor>>();
class Model {public ITxComponent TargetComponent;public ITxKinematicsModellable TargetKinematics;public ITxDevice TargetDevice;
public Vec3 WorldO,WorldA,WorldC,PlaneNormal,WorldGunTcp;public bool HasO,HasA,HasC,OpenStateAdapt;public ITxObject ObjStaticTip,ObjMovingTip;public TxFrame TcpFrame;
public List<ITxObject> FixedLinkBodies=new List<ITxObject>(),InputLinkBodies=new List<ITxObject>(),CouplerLinkBodies=new List<ITxObject>(),OutputLinkBodies=new List<ITxObject>(),Lnk2Bodies=new List<ITxObject>();public string J1_Name,J2_Name,InputJ1_Name;}
Model _model=new Model();struct Vec3{public Vec3(double x,double y,double z){}}
static Panel MkScroll(){return new Panel{Dock=DockStyle.Fill,Padding=new Padding(6)};}
static Button NewBtn(string s){return FormUiKit.MkFuncButton(s,Theme.BtnSecondary);}static Label MakeLabel(string s){return new Label{Text=s,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleRight,Padding=new Padding(0,0,6,0),Font=FormUiKit.BaseFont};}
void BindHingeGrid(TxObjGridCtrl g,Action<Vec3> set,Action clear,Action<Vec3> normal=null){}
void TryUpdateErrors(){}void ClearErr(Label l){}void UpdateModelingStatus(){}void EnableModeling(){}bool TestAncestorReliable(ITxComponent c){return false;}
void OnLinkGridLeave(TxObjGridCtrl g){}void RefreshAllLinkHighlights(){}void RemoveObjectFromGrid(TxObjGridCtrl g,ITxObject o){}
static void WireGridPick(TxObjGridCtrl g){}void OnParamChanged(){}void RefreshFormulaPreview(){}void GoNext(){}void GoBack(){}void DoGenerate(){}
void SyncOpenGapFromCalc(){}
public void SelectStep(int step){_dpiApplied=true;_currentStep=step;UpdateStepUi();}
public WizardFixture(){Font=FormUiKit.BaseFont;AutoScaleMode=AutoScaleMode.None;ClientSize=new Size(884,602);BuildUi();}
}
'''
sdk_colors=sorted(set(re.findall(r'PsSdkHelper\.(Color\w+)',wizard_ui)))
common+='static class PsSdkHelper{public static void UnhighlightAll(params object[] args){}public static void HighlightAll(params object[] args){}'+''.join('public static TxColor '+n+'=new TxColor();' for n in sdk_colors)+'}\n'

fixture('SnakeFixture','SnakeGame/SnakeGameForm.cs','InitUi MakeLabel',
 '''const int InitialTickMs=150;void StartNewGame(){}void TogglePause(){}void ClearAll(){}
 ''','InitUi();')
