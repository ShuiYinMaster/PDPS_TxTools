using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using Tecnomatix.Engineering;

namespace TxTools.AutoRecorder
{
    public enum ShotKind { Fixed, Orbit, Dolly, Move }
    public enum ShotTrigger { Time, Location, Previous }

    public sealed class CameraShot
    {
        public Guid Id = Guid.NewGuid();
        public string Name = "镜头";
        public bool Enabled = true;
        internal bool IsLegacy;
        public ShotKind Kind;
        public ShotTrigger Trigger;
        public double Start;
        public int LocationIndex = 1;
        public double Duration = 3;
        public bool Smooth = true;
        public TxCamera From, To;
        public Image FromThumbnail, ToThumbnail;
        public string FocusLabel;
        public TxVector Focus;
        public ITxLocatableObject FocusObject;
        public bool Follow;
        public int FocusSource;
        public int Axis = 2;
        public double Angle = 180;
        public double Radius = 1500, Height = 600, StartAngle;
        public bool OrbitInitialized;
        public double EndDistance = 500;
        public CameraShot Clone(bool clearObjectBinding=false)
        {
            var copy=(CameraShot)MemberwiseClone();
            copy.Id=Guid.NewGuid();copy.From=Snapshot(From);copy.To=Snapshot(To);
            copy.Focus=Focus==null?null:Copy(Focus);
            copy.FromThumbnail=FromThumbnail==null?null:(Image)FromThumbnail.Clone();
            copy.ToThumbnail=ToThumbnail==null?null:(Image)ToThumbnail.Clone();
            if(clearObjectBinding&&FocusSource>0){copy.FocusObject=null;copy.Focus=null;}
            return copy;
        }
        public void DisposeThumbnails()
        {FromThumbnail?.Dispose();ToThumbnail?.Dispose();FromThumbnail=ToThumbnail=null;}
        public void InitializeOrbit()
        {
            if(From==null || Focus==null)return;
            var v=Sub(From.Position,Focus);
            double x=Axis==0?v.Y:v.X, y=Axis==2?v.Y:v.Z;
            Radius=Math.Sqrt(x*x+y*y);
            Height=Axis==0?v.X:Axis==1?v.Y:v.Z;
            StartAngle=Math.Atan2(y,x)*180/Math.PI;
            OrbitInitialized=true;
        }
        public override string ToString()
        {
            string trigger = Trigger == ShotTrigger.Time ? Start.ToString("0.###") + " s"
                : Trigger == ShotTrigger.Location ? "Location #" + LocationIndex : "按顺序衔接";
            return Name + " · " + trigger + " · " + Duration.ToString("0.###") + " s";
        }

        public static TxVector Copy(TxVector v) { return new TxVector(v.X, v.Y, v.Z); }
        public static TxCamera Snapshot(TxCamera c)
        { return c == null ? null : new TxCamera(Copy(c.ReferencePoint), Copy(c.Position), Copy(c.UpVector)); }
        private static TxVector Mix(TxVector a, TxVector b, double t)
        { return new TxVector(a.X + (b.X-a.X)*t, a.Y+(b.Y-a.Y)*t, a.Z+(b.Z-a.Z)*t); }
        private static double Length(TxVector v) { return Math.Sqrt(v.X*v.X+v.Y*v.Y+v.Z*v.Z); }
        private static TxVector Sub(TxVector a, TxVector b) { return new TxVector(a.X-b.X,a.Y-b.Y,a.Z-b.Z); }
        private static TxVector Cross(TxVector a, TxVector b)
        { return new TxVector(a.Y*b.Z-a.Z*b.Y,a.Z*b.X-a.X*b.Z,a.X*b.Y-a.Y*b.X); }
        private static TxVector Unit(TxVector v)
        { double n=Length(v); if(n<1e-8) throw new InvalidOperationException("相机方向退化，请调整起止视角"); return new TxVector(v.X/n,v.Y/n,v.Z/n); }
        private static TxVector Rotate(TxVector v, int axis, double a)
        {
            double c=Math.Cos(a), s=Math.Sin(a);
            if(axis==0) return new TxVector(v.X,v.Y*c-v.Z*s,v.Y*s+v.Z*c);
            if(axis==1) return new TxVector(v.X*c+v.Z*s,v.Y,-v.X*s+v.Z*c);
            return new TxVector(v.X*c-v.Y*s,v.X*s+v.Y*c,v.Z);
        }
        public TxCamera Evaluate(double progress)
        {
            if(From==null) throw new InvalidOperationException(Name+"：请先记录起始视角");
            foreach(double value in new[]{Start,Duration,Angle,Radius,Height,StartAngle,EndDistance,progress})
                if(double.IsNaN(value)||double.IsInfinity(value))throw new InvalidOperationException("镜头包含无效数值");
            ValidateCamera(From);if(To!=null)ValidateCamera(To);
            double t=Math.Max(0,Math.Min(1,progress)); if(Smooth) t=t*t*(3-2*t);
            if(Kind==ShotKind.Fixed) return Snapshot(From);
            if(Kind==ShotKind.Move)
            {
                if(To==null) throw new InvalidOperationException(Name+"：请记录结束视角");
                if(t==1) return Snapshot(To);
                var p=Mix(From.Position,To.Position,t); var target=Mix(From.ReferencePoint,To.ReferencePoint,t);
                var forward=Unit(Sub(target,p)); var up=Mix(From.UpVector,To.UpVector,t);
                var right=Cross(forward,up);
                if(Length(right)<1e-6) right=Cross(forward, Math.Abs(forward.Z)<0.9 ? new TxVector(0,0,1) : new TxVector(0,1,0));
                return new TxCamera(target,p,Unit(Cross(Unit(right),forward)));
            }
            if(FocusSource>0 && (FocusObject==null || !FocusObject.IsValid())) throw new InvalidOperationException(Name+"：焦点对象已失效");
            var focus=Follow && FocusObject!=null ? FocusObject.AbsoluteLocation.Translation : Focus;
            if(focus==null) throw new InvalidOperationException(Name+"：请选择焦点");
            var offset=Sub(From.Position,Focus ?? focus);
            if(Length(offset)<1e-6) throw new InvalidOperationException("相机不能与焦点重合");
            TxVector upVector=Copy(From.UpVector);
            if(Kind==ShotKind.Orbit)
            {
                if(OrbitInitialized)
                {
                    if(Radius<=0)throw new InvalidOperationException("环绕半径必须大于零");
                    double a=StartAngle*Math.PI/180, x=Radius*Math.Cos(a), y=Radius*Math.Sin(a);
                    offset=Axis==0?new TxVector(Height,x,y):Axis==1?new TxVector(x,Height,y):new TxVector(x,y,Height);
                }
                var planar=Axis==0 ? new TxVector(0,offset.Y,offset.Z) : Axis==1 ? new TxVector(offset.X,0,offset.Z) : new TxVector(offset.X,offset.Y,0);
                if(Length(planar)<1e-6) throw new InvalidOperationException("环绕半径为零，请调整当前视角");
                double angle=Angle*Math.PI/180*t; offset=Rotate(offset,Axis,angle); upVector=Rotate(upVector,Axis,angle);
            }
            else
            {
                double start=Length(offset), distance=start+(EndDistance-start)*t;
                if(EndDistance<=0) throw new InvalidOperationException("结束距离必须大于零");
                offset=new TxVector(offset.X/start*distance,offset.Y/start*distance,offset.Z/start*distance);
            }
            return new TxCamera(Copy(focus),new TxVector(focus.X+offset.X,focus.Y+offset.Y,focus.Z+offset.Z),upVector);
        }
        private static void ValidateCamera(TxCamera camera)
        {
            foreach(var vector in new[]{camera.Position,camera.ReferencePoint,camera.UpVector})
                if(vector==null||new[]{vector.X,vector.Y,vector.Z}.Any(v=>double.IsNaN(v)||double.IsInfinity(v)))
                    throw new InvalidOperationException("视角包含无效坐标");
            var forward=Sub(camera.ReferencePoint,camera.Position);
            if(Length(forward)<1e-8||Length(Cross(forward,camera.UpVector))<1e-8)
                throw new InvalidOperationException("相机位置、目标或上方向退化");
        }
    }

    /// <summary>每次播放创建独立调度器；列表顺序定义事件同刻触发的优先级。</summary>
    internal sealed class CameraShotScheduler
    {
        private readonly IList<CameraShot> _shots;
        private readonly HashSet<Guid> _fired = new HashSet<Guid>();
        private readonly Dictionary<Guid,double> _ends = new Dictionary<Guid,double>();
        private CameraShot _active;
        private double _start, _last = -1;
        public CameraShotScheduler(IList<CameraShot> shots) { _shots=shots.Where(s=>s.Enabled).ToList(); }
        public static void Validate(IList<CameraShot> shots)
        {
            shots=shots.Where(s=>s.Enabled).ToList();
            foreach(var s in shots)
            {
                if(s.Duration<=0 || s.Start<0 || s.LocationIndex<1) throw new InvalidOperationException(s.Name+"：时间或 Location 序号无效");
                s.Evaluate(0); s.Evaluate(0.5); s.Evaluate(1);
            }
            var timed=ShotPlan.Ranges(shots).Where(r=>r.Start.HasValue).OrderBy(r=>r.Start.Value).ToList();
            for(int i=1;i<timed.Count;i++) if(timed[i].Start.Value<timed[i-1].Start.Value+timed[i-1].Shot.Duration-1e-8)
                throw new InvalidOperationException("时间镜头重叠："+timed[i-1].Shot.Name+" / "+timed[i].Shot.Name);
        }
        public TxCamera Tick(double time, int location=0)
        {
            if(time<_last) { _fired.Clear(); _ends.Clear(); _active=null; }
            _last=time;
            TxCamera result=null;
            if(_active!=null) result=_active.Evaluate((time-_start)/_active.Duration);
            for(int pass=0;pass<_shots.Count;pass++)
            {
                CameraShot s=null; double begin=double.MaxValue;
                for(int i=0;i<_shots.Count;i++)
                {
                    var candidate=_shots[i]; if(_fired.Contains(candidate.Id))continue;
                    double at=candidate.Start;
                    bool due=candidate.Trigger==ShotTrigger.Time && time>=at;
                    if(candidate.Trigger==ShotTrigger.Location) {at=time;due=location>0&&location==candidate.LocationIndex;}
                    if(candidate.Trigger==ShotTrigger.Previous&&i>0)due=_ends.TryGetValue(_shots[i-1].Id,out at)&&time>=at;
                    if(candidate.Trigger==ShotTrigger.Previous&&i==0){at=0;due=time>=0;}
                    if(due && (at<begin||(at==begin&&candidate.IsLegacy&&s!=null&&!s.IsLegacy))){s=candidate;begin=at;}
                }
                if(s==null)break;
                if(_active!=null && _ends[_active.Id]>begin) _ends[_active.Id]=begin;
                _fired.Add(s.Id); _ends[s.Id]=begin+s.Duration; _active=s; _start=begin;
                result=s.Evaluate((time-begin)/s.Duration);
            }
            return result;
        }
    }
}
