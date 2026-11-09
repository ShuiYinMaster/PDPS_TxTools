using System.Drawing;
using System.Windows.Forms;

namespace TxTools.AutoRecorder
{
    /// <summary>录像工作区共享布局，宿主和 GUI 预览使用同一控件树。</summary>
    internal sealed class RecorderWorkspace : UserControl
    {
        private readonly TableLayoutPanel _root;
        private readonly Label _steps;
        internal void SetQuickMode(bool quick)
        {_steps.Text=quick?"快速录像：1 选择操作   →   2 选择输出   →   3 开始录制（无需添加镜头）":"编排镜头：1 选择操作   →   2 按类型添加   →   3 选焦点并记录视角   →   4 整段预演   →   5 录制";}
        internal void AttachStatus(Control status)
        {
            status.Dock=DockStyle.Fill;
            status.Margin=Padding.Empty;
            status.MinimumSize=new Size(0,22);
            _root.Controls.Add(status,0,5);
        }
        internal static TableLayoutPanel OutputBar(Label pathLabel, TextBox path, Button browse, NumericUpDown fps, Button advanced)
        {
            var output=new TableLayoutPanel {AutoSize=true,ColumnCount=7,RowCount=1,BackColor=Color.White,Padding=new Padding(8)};
            output.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));output.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            output.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,82));output.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            output.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,72));output.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));output.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,100));
            pathLabel.AutoSize=true;pathLabel.ForeColor=Color.FromArgb(40,60,80);pathLabel.Anchor=AnchorStyles.Left;
            path.Dock=DockStyle.Fill;browse.Dock=DockStyle.Fill;fps.Dock=DockStyle.Fill;advanced.Dock=DockStyle.Fill;
            output.Controls.Add(pathLabel,0,0);output.Controls.Add(path,1,0);output.Controls.Add(browse,2,0);
            output.Controls.Add(new Label {Text="帧率",AutoSize=true,Anchor=AnchorStyles.Left},3,0);output.Controls.Add(fps,4,0);
            output.Controls.Add(new Label {Text="fps",AutoSize=true,Anchor=AnchorStyles.Left},5,0);output.Controls.Add(advanced,6,0);
            return output;
        }
        public RecorderWorkspace(Control operations, Control editor, Control output, Control actions, Control log)
        {
            Dock = DockStyle.Fill;
            BackColor = Color.FromArgb(244,247,250);
            var root = new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=1, RowCount=6, Padding=new Padding(8) };
            _root=root;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,26));
            root.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _steps=new Label {Dock=DockStyle.Fill,Padding=new Padding(6,6,0,0),ForeColor=Color.FromArgb(35,81,116)};
            SetQuickMode(false);root.Controls.Add(_steps,0,0);
            var body = new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty};
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,205));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            body.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            operations.Dock=DockStyle.Fill;editor.Dock=DockStyle.Fill;
            operations.Margin=new Padding(0,0,6,0);
            body.Controls.Add(operations,0,0);body.Controls.Add(editor,1,0);
            root.Controls.Add(body,0,1);
            foreach(var control in new[]{output,actions,log}) {control.Dock=DockStyle.Fill;control.Margin=new Padding(0,6,0,0);}
            actions.MinimumSize=new Size(0,40);
            root.Controls.Add(output,0,2);root.Controls.Add(actions,0,3);root.Controls.Add(log,0,4);
            Controls.Add(root);
        }
    }
}
