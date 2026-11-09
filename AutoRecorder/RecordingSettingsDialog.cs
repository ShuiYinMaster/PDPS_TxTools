using System;
using System.Drawing;
using System.Windows.Forms;

namespace TxTools.AutoRecorder
{
    internal sealed class RecordingSettingsDialog : Form
    {
        private readonly Control _storage;
        private readonly Control[] _settings;
        private readonly TableLayoutPanel _fields;
        private readonly FlowLayoutPanel _footer;

        public RecordingSettingsDialog(Control storage, Font font, ComboBox codec, ComboBox source,
            NumericUpDown compression, CheckBox custom, NumericUpDown width, NumericUpDown height,
            ComboBox speed, CheckBox focus)
        {
            _storage=storage;
            _settings=new Control[]{codec,source,compression,custom,width,height,speed,focus};
            Text="录像高级设置";
            Font=font;
            AutoScaleMode=AutoScaleMode.Dpi;
            AutoScaleDimensions=new SizeF(96,96);
            ClientSize=new Size(380,310);
            StartPosition=FormStartPosition.CenterParent;
            FormBorderStyle=FormBorderStyle.FixedDialog;
            MinimizeBox=false;MaximizeBox=false;
            BackColor=Color.FromArgb(246,248,250);

            var root=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=2};
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            root.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _fields=new TableLayoutPanel {Dock=DockStyle.Top,AutoSize=true,ColumnCount=2,Padding=new Padding(12,8,12,8)};
            _fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            AddRow("视频编码",codec);AddRow("时间源",source);AddRow("压缩率 / %",compression);
            custom.Text="自定义输出分辨率";custom.AutoSize=true;
            AddWide(custom);
            var dimensions=new TableLayoutPanel {AutoSize=true,ColumnCount=3,RowCount=1,Margin=Padding.Empty};
            dimensions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
            dimensions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            dimensions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
            width.Dock=height.Dock=DockStyle.Fill;
            width.Margin=height.Margin=Padding.Empty;
            dimensions.Controls.Add(width,0,0);
            dimensions.Controls.Add(new Label {Text="×",AutoSize=true,Anchor=AnchorStyles.None,Margin=new Padding(5,0,5,0)},1,0);
            dimensions.Controls.Add(height,2,0);
            AddRow("宽 × 高 / px",dimensions);
            AddRow("输出倍速",speed);
            focus.Text="未设起始视角时自动聚焦操作";focus.AutoSize=true;
            AddWide(focus);
            AddWide(new Label {Text="已记录的起始视角优先。",AutoSize=true,ForeColor=Color.DimGray});

            _footer=new FlowLayoutPanel {Dock=DockStyle.Fill,AutoSize=true,FlowDirection=FlowDirection.RightToLeft,
                WrapContents=false,Padding=new Padding(10,8,10,8),BackColor=Color.FromArgb(232,238,244),Margin=Padding.Empty};
            var done=new Button {Text="完成",AutoSize=true,MinimumSize=new Size(80,28),DialogResult=DialogResult.OK};
            _footer.Controls.Add(done);
            AcceptButton=done;CancelButton=done;
            root.Controls.Add(_fields,0,0);root.Controls.Add(_footer,0,1);
            Controls.Add(root);
        }

        private void AddRow(string text,Control control)
        {
            int row=_fields.RowCount++;
            _fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _fields.Controls.Add(new Label {Text=text,AutoSize=true,Anchor=AnchorStyles.Left,Margin=new Padding(0,6,10,6)},0,row);
            control.Dock=DockStyle.Fill;control.Margin=new Padding(0,4,0,4);
            _fields.Controls.Add(control,1,row);
        }
        private void AddWide(Control control)
        {
            int row=_fields.RowCount++;
            _fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            control.Margin=new Padding(0,5,0,5);control.Anchor=AnchorStyles.Left;
            _fields.Controls.Add(control,0,row);_fields.SetColumnSpan(control,2);
        }
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            PerformLayout();
            int required=_fields.GetPreferredSize(new Size(ClientSize.Width,0)).Height+_footer.PreferredSize.Height;
            if(required>ClientSize.Height) ClientSize=new Size(ClientSize.Width,required);
        }
        protected override void Dispose(bool disposing)
        {
            // 参数控件由录像窗口持有，关闭对话框只释放本次布局。
            if(disposing&&_settings!=null&&_storage!=null&&!_storage.IsDisposed)
                foreach(var control in _settings)
                    if(!control.IsDisposed) _storage.Controls.Add(control);
            base.Dispose(disposing);
        }
    }
}
