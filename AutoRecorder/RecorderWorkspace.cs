using System.Drawing;
using System.Linq;
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
            _root.Controls.Add(status,0,4);
        }
        internal static TableLayoutPanel OutputBar(Label pathLabel, TextBox path, Button browse, NumericUpDown fps, Button advanced)
        {
            var output=new TableLayoutPanel {Name="outputFileRow",AutoSize=true,ColumnCount=7,RowCount=1,BackColor=Color.White,Padding=Padding.Empty,Margin=Padding.Empty};
            output.RowStyles.Add(new RowStyle(SizeType.Absolute,32));
            output.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));output.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            output.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));output.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            output.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,66));output.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));output.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pathLabel.AutoSize=true;pathLabel.ForeColor=Color.FromArgb(49,75,96);pathLabel.Anchor=AnchorStyles.Left;pathLabel.Margin=new Padding(0,0,10,0);
            path.Dock=DockStyle.None;path.Anchor=AnchorStyles.Left|AnchorStyles.Right;path.Margin=new Padding(0,0,8,0);
            browse.AutoSize=true;advanced.AutoSize=true;
            foreach(var button in new[]{browse,advanced})
            {
                button.Dock=DockStyle.None;button.Anchor=AnchorStyles.Left;button.Margin=new Padding(0,0,8,0);button.FlatStyle=FlatStyle.Flat;
                button.BackColor=Color.White;button.ForeColor=Color.FromArgb(49,75,96);button.FlatAppearance.BorderColor=Color.FromArgb(207,219,229);button.FlatAppearance.MouseOverBackColor=Color.FromArgb(235,245,250);button.UseVisualStyleBackColor=false;
            }
            bool aligning=false;
            output.Layout+=(sender,e)=>{if(aligning)return;aligning=true;try{foreach(var button in new[]{browse,advanced})if(button.Anchor!=AnchorStyles.Left)button.Anchor=AnchorStyles.Left;}finally{aligning=false;}};
            advanced.Margin=Padding.Empty;fps.Dock=DockStyle.None;fps.Anchor=AnchorStyles.Left|AnchorStyles.Right;fps.Margin=new Padding(0,0,4,0);
            output.Controls.Add(pathLabel,0,0);output.Controls.Add(path,1,0);output.Controls.Add(browse,2,0);
            output.Controls.Add(new Label {Text="帧率",AutoSize=true,ForeColor=Color.FromArgb(91,110,125),Anchor=AnchorStyles.Left,Margin=new Padding(6,0,6,0)},3,0);output.Controls.Add(fps,4,0);
            output.Controls.Add(new Label {Text="fps",AutoSize=true,ForeColor=Color.FromArgb(91,110,125),Anchor=AnchorStyles.Left,Margin=new Padding(0,0,12,0)},5,0);output.Controls.Add(advanced,6,0);
            return output;
        }
        public RecorderWorkspace(Control operations, Control editor, Control output, Control actions, Control log)
        {
            Dock = DockStyle.Fill;
            BackColor = Color.FromArgb(244,247,250);
            var root = new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=1, RowCount=5, Padding=new Padding(8) };
            _root=root;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,26));
            root.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _steps=new Label {Dock=DockStyle.Fill,Padding=new Padding(8,6,0,0),BackColor=Color.FromArgb(228,239,245),ForeColor=Color.FromArgb(26,77,98)};
            SetQuickMode(false);root.Controls.Add(_steps,0,0);
            var body = new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty};
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,205));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            body.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            operations.Dock=DockStyle.Fill;editor.Dock=DockStyle.Fill;
            operations.Margin=new Padding(0,0,6,0);
            body.Controls.Add(operations,0,0);body.Controls.Add(editor,1,0);
            root.Controls.Add(body,0,1);
            var footer=new TableLayoutPanel{Name="recordingFooter",Dock=DockStyle.Fill,AutoSize=true,ColumnCount=1,RowCount=2,Padding=new Padding(10,7,10,7),Margin=new Padding(0,6,0,0),BackColor=Color.White};
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            footer.Paint+=(sender,e)=>{using(var line=new Pen(Color.FromArgb(215,226,234)))e.Graphics.DrawLine(line,0,0,footer.Width,0);};
            output.Dock=DockStyle.Fill;output.Margin=Padding.Empty;footer.Controls.Add(output,0,0);
            var bottom=new TableLayoutPanel{Name="recordingActionRow",Dock=DockStyle.Fill,AutoSize=true,ColumnCount=2,RowCount=1,Margin=new Padding(0,4,0,0),Padding=Padding.Empty};
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var readiness=actions.Controls.OfType<Label>().FirstOrDefault();
            if(readiness!=null){readiness.Dock=DockStyle.Fill;readiness.Margin=new Padding(0,0,8,0);readiness.AutoSize=false;readiness.AutoEllipsis=true;readiness.TextAlign=ContentAlignment.MiddleLeft;bottom.Controls.Add(readiness,0,0);}
            var actionFlow=actions as FlowLayoutPanel;if(actionFlow!=null){actionFlow.WrapContents=false;actionFlow.AutoSize=true;actionFlow.AutoSizeMode=AutoSizeMode.GrowAndShrink;}
            actions.MinimumSize=Size.Empty;actions.Padding=Padding.Empty;actions.Margin=Padding.Empty;actions.Dock=DockStyle.Fill;actions.BackColor=Color.White;
            bottom.Controls.Add(actions,1,0);footer.Controls.Add(bottom,0,1);root.Controls.Add(footer,0,2);
            log.Dock=DockStyle.Fill;log.Margin=new Padding(0,6,0,0);root.Controls.Add(log,0,3);
            Controls.Add(root);
        }
    }
}
