using System;
using System.Drawing;
using System.Windows.Forms;
using Tecnomatix.Engineering.Ui;
using TxTools.Common;

namespace TxTools.CrossEnvIO
{
    // SDK editors change their default size while their handles are being initialized.
    // Give the native root an explicit row and keep the caption outside that row.
    internal sealed class CrossEnvNativeFrame : FormUiKit.ColoredGroupBox
    {
        private readonly Control _picker;
        private int _nativeHeight, _top, _side, _bottom;
        private bool _arranging;
        internal CrossEnvNativeFrame(string title,Control picker)
        {
            Text=title;
            Font=FormUiKit.BoldFont;
            TitleColor=FormUiKit.Theme.TxCol1.Color;
            BackColor=FormUiKit.CardBack;
            AutoSize=false;
            Margin=new Padding(6,5,6,7); Padding=Padding.Empty;
            _picker=picker; picker.Dock=DockStyle.None; picker.Anchor=AnchorStyles.Top|AnchorStyles.Left;
            Controls.Add(picker);
            picker.HandleCreated+=(s,e)=>RefreshMetrics(DeviceDpi);
            picker.SizeChanged+=(s,e)=>PerformLayout();
            picker.LocationChanged+=(s,e)=>PerformLayout();
            if(picker is UserControl view)view.Load+=(s,e)=>RefreshMetrics(DeviceDpi);
            RefreshMetrics(DeviceDpi);
        }
        internal void RefreshMetrics(int dpi)
        {
            double scale=Math.Max(1,dpi/96.0);
            _nativeHeight=_picker is TxObjGridCtrl?CrossEnvPickForm.NativeGridHeight(_picker.Font,dpi)
                :Math.Max((int)Math.Ceiling(28*scale),_picker.Font.Height+(int)Math.Ceiling(10*scale));
            _side=(int)Math.Ceiling(10*scale); _bottom=(int)Math.Ceiling(8*scale);
            _top=Font.Height+(int)Math.Ceiling(8*scale);
            int height=_top+_nativeHeight+_bottom;
            int width=Math.Max(1,Width);
            MinimumSize=new Size(0,height); MaximumSize=new Size(MaximumSize.Width,height); Size=new Size(width,height);
            PerformLayout();
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            if(_arranging||_picker==null)return;
            _arranging=true;
            try
            {
                base.OnLayout(e);
                foreach(Control child in Controls)
                {
                    child.AutoSize=false; child.Dock=DockStyle.None;
                    int width=Math.Max(1,ClientSize.Width-2*_side);
                    child.MinimumSize=new Size(0,_nativeHeight); child.MaximumSize=new Size(width,_nativeHeight);
                    child.SetBounds(_side,_top,width,_nativeHeight);
                }
            }
            finally { _arranging=false; }
        }
    }
}
