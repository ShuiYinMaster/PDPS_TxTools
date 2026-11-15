using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using TxTools.Common;

namespace TxTools.AutoRecorder
{
    internal enum TransportIcon { PlayShot, PlayOperation, Pause, Resume, Stop, RestoreView, SavePlan, LoadPlan, ApplyPlan, DuplicateShot, DeleteShot, MoveUp, MoveDown }

    /// <summary>以矢量路径绘制播放和编辑工具，避免图标字体缺失和 DPI 位图模糊。</summary>
    internal sealed class TransportIconButton : Button,ICompactIconButton
    {
        private TransportIcon _icon;
        private readonly Size _designSize;
        private bool _active,_hover,_pressed;
        public Size DesignButtonSize {get {return _designSize;}}
        public TransportIcon Icon
        {get {return _icon;}set {if(_icon==value)return;_icon=value;Invalidate();}}
        public bool Active
        {get {return _active;}set {if(_active==value)return;_active=value;Invalidate();}}

        public TransportIconButton(TransportIcon icon,bool compact=false)
        {
            _designSize=compact?new Size(26,26):new Size(34,30);
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
            var accent=(_icon==TransportIcon.Stop||_icon==TransportIcon.DeleteShot)?Color.FromArgb(174,56,56):_icon==TransportIcon.PlayOperation?Color.FromArgb(0,113,121):Color.FromArgb(27,96,150);
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
                    case TransportIcon.SavePlan:
                        e.Graphics.DrawRectangle(pen,4,3,16,18);e.Graphics.DrawRectangle(pen,8,3,9,6);e.Graphics.DrawRectangle(pen,7,13,10,8);break;
                    case TransportIcon.LoadPlan:
                        e.Graphics.DrawLines(pen,new[]{new PointF(3,19),new PointF(3,6),new PointF(10,6),new PointF(12,9),new PointF(21,9),new PointF(19,20),new PointF(3,20)});
                        e.Graphics.DrawLine(pen,13,3,13,14);e.Graphics.DrawLines(pen,new[]{new PointF(9,11),new PointF(13,15),new PointF(17,11)});break;
                    case TransportIcon.ApplyPlan:
                        e.Graphics.DrawRectangle(pen,2,4,7,13);e.Graphics.DrawRectangle(pen,16,8,6,13);
                        e.Graphics.DrawLine(pen,7,5,18,5);e.Graphics.DrawLines(pen,new[]{new PointF(15,2),new PointF(18,5),new PointF(15,8)});break;
                    case TransportIcon.DuplicateShot:
                        e.Graphics.DrawLines(pen,new[]{new PointF(7,17),new PointF(3,17),new PointF(3,3),new PointF(16,3),new PointF(16,7)});
                        e.Graphics.DrawRectangle(pen,8,8,13,13);break;
                    case TransportIcon.DeleteShot:
                        e.Graphics.DrawLine(pen,3,6,21,6);e.Graphics.DrawRectangle(pen,8,3,8,3);
                        e.Graphics.DrawLines(pen,new[]{new PointF(5,7),new PointF(6,21),new PointF(18,21),new PointF(19,7)});
                        e.Graphics.DrawLine(pen,10,10,10,17);e.Graphics.DrawLine(pen,14,10,14,17);break;
                    case TransportIcon.MoveUp:
                    case TransportIcon.MoveDown:
                        bool up=_icon==TransportIcon.MoveUp;
                        e.Graphics.DrawLine(pen,12,up?20:4,12,up?4:20);
                        e.Graphics.DrawLines(pen,new[]{new PointF(6,up?10:14),new PointF(12,up?4:20),new PointF(18,up?10:14)});break;
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
