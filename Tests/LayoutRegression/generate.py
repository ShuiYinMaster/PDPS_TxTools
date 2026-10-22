"""Extract production UI builders; replace only scene/business boundaries in fixtures."""
from pathlib import Path
import re

root = Path(__file__).resolve().parents[2]

def source(path):
    return (root / path).read_text(encoding='utf-8-sig')

def block(text, pattern):
    start = re.search(pattern, text, re.M).start()
    masked = re.sub(r'//[^\n]*|/\*[\s\S]*?\*/|@"(?:""|[^"])*"|"(?:\\.|[^"\\])*"', lambda m: ' ' * len(m[0]), text)
    opening = masked.index('{', start)
    depth = 1
    end = opening + 1
    while depth:
        depth += (masked[end] == '{') - (masked[end] == '}')
        end += 1
    return text[start:end]

def methods(text, names):
    return '\n'.join(block(text, r'^        (?:public|private|internal|protected) [^\n{;]*\b' + name + r'\(') for name in names.split())

def fields(text):
    return '\n'.join(re.findall(r'^        private (?:readonly )?(?:(?:FormUiKit\.)?(?:FlatColorButton|FlatColorLabel)|TabControl|TabPage|StatusStrip|ToolStripStatusLabel|ToolStripButton|ToolTip|TreeView|Control|NumericUpDown|CheckBox|FlowLayoutPanel|Panel|Button|Label|ComboBox|TextBox|RichTextBox|RadioButton|TableLayoutPanel|ListView|DataGridView|ProgressBar|Color|Font)\s+[^\n;]*;', text, re.M))

kit = source('SRC/FormUiKit.cs')
common = '''using System; using System.Drawing; using System.Windows.Forms; using System.Linq; using System.Collections.Generic;using System.IO;
using TxTools.Common; using static TxTools.Common.FormUiKit;
namespace TxTools.Common { public static class FormUiKit {
public static Font BaseFont = new Font("Microsoft YaHei UI", 9), BoldFont = new Font(BaseFont, FontStyle.Bold), MonoFont = BaseFont;
public static Color CardBack=Color.White, CardBorder=Color.Gray, WinBg=Color.White;
public static Color PrimaryBack=Color.SteelBlue,PrimaryFore=Color.White;
public static Font TitleFont=BoldFont; public static Color TitleBack=Color.LightGray,TitleFore=Color.Black;
public class FlatColorLabel:Label{}
public class FlatColorButton:Button {public Color BgColor{get{return BackColor;}set{BackColor=value;}}public Color BorderColor{get;set;}public Color HoverColor{get;set;}}
public static class Theme {public static Color BtnPrimary=Color.Blue,BtnSecondary=Color.Gray,BtnMuted=Color.Gray,CardTitle=Color.Black,BtnFore=Color.Black,BtnBorder=Color.Gray,BtnHover=Color.LightGray,HeaderFore=Color.Black;}
public static float TestScale=1; public static float GetLayoutScale(Control c){return TestScale;}
public static class GridPickFocus {public static void Wire(Control c){}}
'''
common += block(kit, r'        public sealed class ColoredGroupBox')
common += methods(kit, 'MkCardGroup MkCardContent MkFieldLabel MkRowFlow MkRadio MkCheckBox MkTextBox MkComboBox MkNumeric FillWidthInFlow WrapLabelInFlow AutoFitComboBoxWidth AutoFitNumericWidth MkFuncButton MkCard BuildCardColumn BuildRoot MkBtn MkLabel MkButton')
common += '''} }
static class Theme {
public static Color LogOk=Color.Green,LogErr=Color.Red,LogWarn=Color.Orange,LogPs=Color.Blue,LogCoord=Color.Blue,LogExcel=Color.Green;
public static Color BtnFore=Color.Black, BtnGun=Color.Blue, BtnMuted=Color.Gray, BtnExport=Color.Green, BtnDanger=Color.Red, BtnPrimary=Color.Blue, BtnSecondary=Color.Gray,
InputBg=Color.White, TextDim=Color.Gray, StatusOkFg=Color.Green, StatusOkBg=Color.White, CardTitle=Color.Black, TextFaint=Color.Gray, LogBg=Color.White, LogText=Color.Black,
FenceMesh=Color.Gray, FenceFrame=Color.Gray, FencePost=Color.Gray, FenceBaseplate=Color.Gray;
public static TxColor TxLog=new TxColor(), TxCol1=new TxColor(), TxBall=new TxColor(), TxAccent=new TxColor(), TxGun=new TxColor(), TxPoints=new TxColor();
}
class Tint { public Color Color=Color.Black; }
class TxColor:Tint { public static TxColor TxColorWhite=new TxColor{Color=Color.White}; }
class TxToolStrip:ToolStrip{}
class TxObjGridCtrl:Panel {public bool ListenToPick{get;set;}public bool EnableMultipleSelection{get;set;}public bool EnableRecurringObjects{get;set;}
public TxPickLevel PickLevel{get;set;}public object GetObject(int row){return null;}
public List<object> Objects{get;set;}=new List<object>();public int Count{get{return Objects.Count;}}
public event EventHandler SelectionChanged{add{}remove{}}
public event TxObjGridCtrl_ObjectInsertedEventHandler ObjectInserted{add{}remove{}}public event TxObjGridCtrl_RowDeletedEventHandler RowDeleted{add{}remove{}}}
delegate void TxObjGridCtrl_ObjectInsertedEventHandler(object s,EventArgs e);delegate void TxObjGridCtrl_RowDeletedEventHandler(object s,EventArgs e);
class TxObjEditBoxCtrl:TextBox {public bool ListenToPick{get;set;}public bool PickOnly{get;set;}public event EventHandler Picked{add{}remove{}}}
static class PickFocus{public static void Wire(Control c){}}
enum TxPickLevel{Entity,Component}
class ITxObject{public string Name;}class ITxComponent:ITxObject{}class ITxKinematicsModellable:ITxObject{}class ITxDevice:ITxObject{}class TxFrame:ITxObject{}
class TxTransformation{}
class TxFrameEditBoxCtrl:TextBox{public TxPickLevel PickLevel{get;set;}public bool ListenToPick{get;set;}public event EventHandler ValidFrameSet{add{}remove{}}public event EventHandler InvalidFrameSet{add{}remove{}}public event EventHandler Picked{add{}remove{}}}
'''
fence = source('AutoFance/FenceBuilderForm.cs')
common += 'class FenceFixture : Form {\n' + fields(fence)
common += methods(fence, 'BuildMeshCard BuildPostCard NewCard NewCardFlow AddParamRow AddColorRow')
common += '''public FenceFixture() { Font=FormUiKit.BaseFont; AutoScaleMode=AutoScaleMode.None; ClientSize=new Size(320,560);
var side=new FlowLayoutPanel {Dock=DockStyle.Fill, FlowDirection=FlowDirection.TopDown,WrapContents=false};
side.Controls.Add(BuildMeshCard()); side.Controls.Add(BuildPostCard()); Controls.Add(side); } }
'''
gun = source('ExportGun/ExportGunForm.cs')
gun_design = re.search(r'private static readonly Size DesignSize = new Size\((\d+), (\d+)\)', gun)
assert gun_design, 'ExportGun design size must come from production'
common += 'class GunFixture : Form {\n' + fields(gun)
common += methods(gun, 'BuildHeaderRow BuildLogPanel BuildBody BuildCol1 BuildOpsCard BuildCoordCard BuildSettingsCard BuildPointsColumn BuildBottom BuildCol2 BuildGunCard BuildBallCard MkCardContent MkLabel MkRowFlow MkFuncButton AutoFitComboBoxWidth AutoFitNumericWidth WrapLabelInFlow FillWidthInFlow')
common += methods(gun, 'Log GuessLevel ToggleLog DpiScale')
common += block(gun, r'        private void Log\(string msg, LogLevel level\)')
common += block(gun, r'        private void Log\(string msg, LogLevel level, bool expandOnError\)')
common += '\n'.join(re.findall(r'^        private const int LOG_.*;',gun,re.M))
common += '''static GroupBox MkCard(string s, Tint t) { return FormUiKit.MkCardGroup(s,t.Color); }
static Label MkHeaderLabel(string s,TxColor c){return new Label{Text=s,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleCenter,Font=FormUiKit.BoldFont,BackColor=Color.LightGray};}
bool _logExpanded; float _layoutScale {get{return FormUiKit.TestScale;}}
public bool LogExpanded {get{return _logExpanded;}}
public Label GunSummary {get{return _lblGunInfo;}}
public Button ExportButton {get{return _btnExportGun;}}
public void WriteBackendLog(string text){Log(text);}
static class PsReader { public const string DefaultTcpLabel="默认TCP"; }
class TxFrameComboBoxCtrl : TextBox {public bool ListenToPick{get;set;} }
class TxObjGridCtrl : Panel {public bool ListenToPick{get;set;} public bool EnableMultipleSelection{get;set;} public bool EnableRecurringObjects{get;set;}
public event TxObjGridCtrl_ObjectInsertedEventHandler ObjectInserted {add{}remove{}} public event TxObjGridCtrl_RowDeletedEventHandler RowDeleted{add{}remove{}} }
delegate void TxObjGridCtrl_ObjectInsertedEventHandler(object s,EventArgs e); delegate void TxObjGridCtrl_RowDeletedEventHandler(object s,EventArgs e);
static class PickFocus {public static void Wire(Control c){} }
interface IOpListAdapter{} class GridAdapter:IOpListAdapter {public GridAdapter(GunFixture f){} } class ListViewAdapter:IOpListAdapter {public ListViewAdapter(GunFixture f){} }
IOpListAdapter _listAdapter; TxObjGridCtrl _objGrid; TxFrameComboBoxCtrl _frameCombo; bool _dpiApplied=true; string _refFrameName;double[] _refFrameMatrix;
enum LogLevel{Info,Ok,Warn,Error,Ps,Coord,Excel,Debug} void UpdateRefCoordStatus(string s,bool b){} void RebuildPointsListView(){} void UpdatePointCount(){} void ReloadPointsList(){}
void OnGridObjectInserted(object s,EventArgs e){} void OnGridRowDeleted(object s,EventArgs e){} void OnPickSel(object s,EventArgs e){} void OnClearOps(object s,EventArgs e){}
void OnPointItemChecked(object s,ItemCheckedEventArgs e){} void StretchPointsColumn(){} void SetAllPointsChecked(bool? b){}
void OnReset(object s,EventArgs e){} void OnExportExcel(object s,EventArgs e){} void ShowHelp(){}
void OnBrowseGun(object s,EventArgs e){} void OnTcpChoiceChanged(object s,EventArgs e){}
void OnExportGun(object s,EventArgs e){} void OnExportBall(object s,EventArgs e){}
public GunFixture() {Font=FormUiKit.BaseFont;AutoScaleMode=AutoScaleMode.None;ClientSize=new Size(GUN_WIDTH,GUN_HEIGHT);
var root=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=4};_root=root;
root.RowStyles.Add(new RowStyle(SizeType.Absolute,28));root.RowStyles.Add(new RowStyle(SizeType.Percent,100));root.RowStyles.Add(new RowStyle(SizeType.Absolute,26));root.RowStyles.Add(new RowStyle(SizeType.Absolute,46));
var cols=BuildBody();root.Controls.Add(BuildHeaderRow(),0,0);root.Controls.Add(cols,0,1);root.Controls.Add(BuildLogPanel(),0,2);root.Controls.Add(BuildBottom(),0,3);Controls.Add(root); } }
'''
common = common.replace('GUN_WIDTH', str(int(gun_design[1])-16)).replace('GUN_HEIGHT', str(int(gun_design[2])-38))
allocator = source('WeldSpotAllocator/Ui/AllocatorForm.cs')
common += 'class AllocatorFixture : Form {\n' + fields(allocator)
common += methods(allocator, 'BuildUi MkCard MkFixedCard BuildModeCard BuildHint BuildDataCard BuildSide BuildOptionCard BuildActionRow BuildPreviewCard BuildLogCard AddBreak LabeledPair')
common += '''class Host { public void Clear(){} } Host _refHost=new Host(),_tgtHost=new Host();
static RadioButton NewRb(string t){return FormUiKit.MkRadio(t);} static Button NewBtn(string t,Color c){return FormUiKit.MkFuncButton(t,c);}
static CheckBox NewChk(string t){return FormUiKit.MkCheckBox(t);} string RefText(){return "参考操作 12 条";} string TgtText(){return "目标操作 12 条";}
void SyncModeUi(){} void DoPreview(){} void DoApply(){} void DoExport(){} void DoSwap(){}
public Panel Selection { get {return _refHostPanel;} }
public AllocatorFixture(){Font=FormUiKit.BaseFont;AutoScaleMode=AutoScaleMode.None;ClientSize=new Size(824,680);BuildUi();}
}
'''
out = Path(__file__).parent / 'obj/Fixtures.cs'
catia = source('CatiaPartTree/CatiaPartTreeForm.cs')
common += 'class CatiaFixture : Form {\n' + fields(catia)
common += methods(catia, 'BuildTreeCard AddEditButton')
common += '''object _model=new object(); void RefreshTree(){} void CaptureExpansion(){} void SetAllChecked(bool b){}
void OnTreeAfterCheck(object s,TreeViewEventArgs e){} void OnTreeAfterLabelEdit(object s,NodeLabelEditEventArgs e){}
void OnReadTree(){} void OnAddChild(){} void OnAddSibling(){} void OnRename(){} void OnDelete(){} void OnMoveUp(){} void OnMoveDown(){} void OnIndent(){} void OnOutdent(){}
public CatiaFixture(){Font=FormUiKit.BaseFont;AutoScaleMode=AutoScaleMode.None;ClientSize=new Size(700,520);Controls.Add(BuildTreeCard());}
}
'''
out.parent.mkdir(exist_ok=True)
exec((Path(__file__).parent / 'more_fixtures.py').read_text(encoding='utf-8'), globals())
out.write_text(common, encoding='utf-8-sig')
print('Extracted current production UI builders and scene-boundary fixtures')
