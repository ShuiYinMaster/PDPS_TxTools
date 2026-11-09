using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using TxTools.Common;

namespace TxTools.AutoRecorder
{
    internal enum TransportIcon { PlayShot, PlayOperation, Pause, Resume, Stop, RestoreView }

    /// <summary>以矢量路径绘制播放控件，避免图标字体缺失和 DPI 位图模糊。</summary>
    internal sealed class TransportIconButton : Button,ICompactIconButton
    {
        private TransportIcon _icon;
        private bool _active,_hover,_pressed;
        public Size DesignButtonSize {get {return new Size(34,30);}}
        public TransportIcon Icon
        {get {return _icon;}set {if(_icon==value)return;_icon=value;Invalidate();}}
        public bool Active
        {get {return _active;}set {if(_active==value)return;_active=value;Invalidate();}}

        public TransportIconButton(TransportIcon icon)
        {
            _icon=icon;AutoSize=false;Size=DesignButtonSize;
            Text="";FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;
            Margin=new Padding(2,0,2,0);Padding=Padding.Empty;
            AccessibleRole=AccessibleRole.PushButton;
            SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
        }
        protected override void OnMouseEnter(EventArgs e){base.OnMouseEnter(e);_hover=true;Invalidate();}
        protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);_hover=_pressed=false;Invalidate();}
        protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);if(e.Button==MouseButtons.Left){_pressed=true;Invalidate();}}
        protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);_pressed=false;Invalidate();}
        protected override void OnEnabledChanged(EventArgs e){base.OnEnabledChanged(e);_hover=_pressed=false;Invalidate();}
        protected override void OnGotFocus(EventArgs e){base.OnGotFocus(e);Invalidate();}
        protected override void OnLostFocus(EventArgs e){base.OnLostFocus(e);_pressed=false;Invalidate();}
        protected override void OnPaint(PaintEventArgs e)
        {
            var accent=_icon==TransportIcon.Stop?Color.FromArgb(174,56,56):_icon==TransportIcon.PlayOperation?Color.FromArgb(0,113,121):Color.FromArgb(27,96,150);
            var background=!Enabled?(_active?Color.FromArgb(226,238,244):Color.FromArgb(246,247,249)):_active?accent:_pressed?Color.FromArgb(210,228,242):_hover?Color.FromArgb(232,242,250):Color.White;
            var foreground=!Enabled?(_active?accent:Color.FromArgb(170,181,191)):_active?Color.White:accent;
            e.Graphics.Clear(background);
            using(var border=new Pen(Enabled&&(_hover||_active)?accent:Color.FromArgb(199,209,219)))
                e.Graphics.DrawRectangle(border,0,0,Math.Max(0,Width-1),Math.Max(0,Height-1));
            var state=e.Graphics.Save();
            e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            float scale=Math.Min(Width,Height)/30f;
            e.Graphics.TranslateTransform((Width-24*scale)/2,(Height-24*scale)/2);
            e.Graphics.ScaleTransform(scale,scale);
            using(var pen=new Pen(foreground,1.7f))
            using(var brush=new SolidBrush(foreground))
            {
                pen.LineJoin=LineJoin.Round;pen.StartCap=pen.EndCap=LineCap.Round;
                switch(_icon)
                {
                    case TransportIcon.PlayShot:
                    case TransportIcon.Resume:
                        e.Graphics.FillPolygon(brush,new[]{new PointF(8,5),new PointF(19,12),new PointF(8,19)});break;
                    case TransportIcon.Pause:
                        e.Graphics.FillRectangle(brush,6,5,4,14);e.Graphics.FillRectangle(brush,14,5,4,14);break;
                    case TransportIcon.Stop:
                        e.Graphics.FillRectangle(brush,6,6,12,12);break;
                    case TransportIcon.PlayOperation:
                        e.Graphics.DrawRectangle(pen,3,4,18,16);
                        e.Graphics.DrawLine(pen,6,4,6,20);e.Graphics.DrawLine(pen,18,4,18,20);
                        for(int y=7;y<20;y+=5){e.Graphics.DrawLine(pen,3,y,6,y);e.Graphics.DrawLine(pen,18,y,21,y);}
                        e.Graphics.FillPolygon(brush,new[]{new PointF(10,8),new PointF(16,12),new PointF(10,16)});break;
                    case TransportIcon.RestoreView:
                        e.Graphics.DrawArc(pen,2,2,16,16,195,245);
                        e.Graphics.DrawLines(pen,new[]{new PointF(2,2),new PointF(2,7),new PointF(7,7)});
                        e.Graphics.DrawRectangle(pen,9,10,12,9);
                        e.Graphics.DrawEllipse(pen,13,12,4,4);
                        e.Graphics.DrawLine(pen,12,10,14,8);e.Graphics.DrawLine(pen,14,8,17,8);break;
                }
            }
            e.Graphics.Restore(state);
            if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(e.Graphics,new Rectangle(3,3,Math.Max(0,Width-6),Math.Max(0,Height-6)),foreground,background);
        }
    }
}
