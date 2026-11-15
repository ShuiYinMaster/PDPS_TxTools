using System;
using System.Collections.Generic;
using System.Linq;
using Tecnomatix.Engineering;

namespace TxTools.AutoRecorder
{
    internal sealed class ShotRange
    {
        public CameraShot Shot;
        public double? Start;
    }
    internal static class ShotPlan
    {
        public static List<string> Issues(OperationViewSchedule schedule,double? duration,int locationCount)
        {
            if(schedule.Quick)return new List<string>();
            var issues=schedule.Shots.Any(s=>s.Enabled)||!schedule.Keyframes.Any(k=>k?.Camera!=null)
                ? Issues(schedule.Shots,duration,locationCount):new List<string>();
            foreach(var key in schedule.Keyframes.Where(k=>k?.Camera!=null))
                if(key.LocationIndex<1||key.LocationIndex>locationCount)issues.Add("Location #"+key.LocationIndex+" 视角超出操作范围");
            return issues;
        }
        public static List<ShotRange> Ranges(IEnumerable<CameraShot> shots)
        {
            var ranges=new List<ShotRange>();double? end=0;
            foreach(var shot in shots.Where(s=>s.Enabled))
            {
                double? start=shot.Trigger==ShotTrigger.Time?(double?)shot.Start:shot.Trigger==ShotTrigger.Previous?end:null;
                ranges.Add(new ShotRange {Shot=shot,Start=start});end=start+shot.Duration;
            }
            return ranges;
        }
        public static string Missing(CameraShot shot,IList<CameraShot> context=null)
        {
            if(!shot.Enabled)return "已停用";
            if(shot.GeometryCenter&&shot.FocusObject!=null&&shot.Focus==null)return "对象几何中心不可读；请更换对象或取消几何中心";
            if(shot.InheritStart){try{shot=Prepare(shot,context);}catch(Exception ex){return ex.Message;}}
            if(shot.Kind==ShotKind.Follow&&(shot.FocusObject==null||shot.Focus==null))return "待选择跟随资源并记录起点";
            if((shot.Kind==ShotKind.Orbit||shot.Kind==ShotKind.Dolly)&&
                (shot.Focus==null||(shot.FocusSource>0&&shot.FocusObject==null)))return "待选焦点";
            if(shot.From==null)return "待记录起点";
            if(shot.Kind==ShotKind.Move&&shot.To==null)return "待记录终点";
            try{shot.Evaluate(0);shot.Evaluate(.5);shot.Evaluate(1);}catch(Exception ex){return ex.Message;}
            return null;
        }
        public static CameraShot Prepare(CameraShot source,IList<CameraShot> context)
        {
            var shot=source.RuntimeCopy();if(!source.InheritStart)return shot;
            if(context==null)throw new InvalidOperationException("待继承前段末态");
            var before=context.TakeWhile(s=>!ReferenceEquals(s,source)).Where(s=>s.Enabled).ToList();
            if(before.Count==0)throw new InvalidOperationException("首镜头需独立记录起点");
            var range=Ranges(context).FirstOrDefault(r=>ReferenceEquals(r.Shot,source));
            TxCamera from;CameraShot previous;
            if(range!=null&&range.Start.HasValue&&before.All(s=>s.Trigger!=ShotTrigger.Location)){var scheduler=new CameraShotScheduler(before);from=scheduler.Tick(range.Start.Value);previous=scheduler.LastShot;}
            else {previous=Prepare(before.Last(),before);from=previous.Evaluate(1);}
            if(from==null)throw new InvalidOperationException("前段缺少可继承的视角");
            shot.From=CameraShot.Snapshot(from);shot.OrbitInitialized=false;
            InheritFocus(shot,previous,from);
            if(shot.Kind==ShotKind.Follow||(shot.Follow&&shot.FocusObject!=null))shot.CaptureFocus();return shot;
        }
        internal static void InheritFocus(CameraShot shot,CameraShot previous,TxCamera from)
        {
            if(shot.FocusSource!=0||shot.Focus!=null)return;
            if(previous!=null&&previous.FocusSource>0&&previous.FocusObject!=null)
            {
                shot.FocusSource=previous.FocusSource;shot.FocusObject=previous.FocusObject;shot.Focus=previous.Focus;
                shot.FocusLocal=previous.FocusLocal;shot.FocusLabel=previous.FocusLabel;shot.GeometryCenter=previous.GeometryCenter;shot.Follow=previous.Follow;
            }
            else shot.Focus=CameraShot.Copy(from.ReferencePoint);
        }
        public static bool CanCompose(CameraShot a,CameraShot b)
        {
            if(!((a.Kind==ShotKind.Orbit&&b.Kind==ShotKind.Dolly)||(a.Kind==ShotKind.Dolly&&b.Kind==ShotKind.Orbit)))return false;
            if(a.FocusSource>0||b.FocusSource>0)
                return a.FocusSource>0&&b.FocusSource>0&&a.FocusObject!=null&&Equals(a.FocusObject,b.FocusObject)&&a.Follow==b.Follow&&a.GeometryCenter==b.GeometryCenter;
            if(a.Focus==null||b.Focus==null)return (a.Focus==null&&a.InheritStart)||(b.Focus==null&&b.InheritStart);
            return Math.Abs(a.Focus.X-b.Focus.X)<1e-6&&Math.Abs(a.Focus.Y-b.Focus.Y)<1e-6&&Math.Abs(a.Focus.Z-b.Focus.Z)<1e-6;
        }
        public static List<string> Conflicts(IList<CameraShot> shots)
        {
            var result=new List<string>();var ranges=Ranges(shots).Where(r=>r.Start.HasValue&&!r.Shot.IsLegacy).ToList();
            for(int i=0;i<ranges.Count;i++)for(int j=i+1;j<ranges.Count;j++)
            {
                var a=ranges[i];var b=ranges[j];
                if(Math.Max(a.Start.Value,b.Start.Value)>=Math.Min(a.Start.Value+a.Shot.Duration,b.Start.Value+b.Shot.Duration)-1e-8)continue;
                try{if(CanCompose(Prepare(a.Shot,shots),Prepare(b.Shot,shots)))continue;}catch{}
                result.Add(a.Shot.Name+" / "+b.Shot.Name+"：重叠冲突；仅同一焦点的旋转与推拉可以叠加");
            }
            return result;
        }
        public static string Hint(CameraShot shot,IList<CameraShot> shots,double? duration,int locationCount,List<string> conflicts=null)
        {
            var missing=Missing(shot,shots);if(missing!=null)return missing;
            if(shot.Trigger==ShotTrigger.Location&&shot.LocationIndex>locationCount)return "Location 超出操作范围";
            var range=Ranges(shots).FirstOrDefault(r=>ReferenceEquals(r.Shot,shot));
            if(duration.HasValue&&range?.Start+shot.Duration>duration.Value+.001)return "超出操作预计时长";
            return (conflicts??Conflicts(shots)).FirstOrDefault(s=>s.StartsWith(shot.Name+" / ")||s.Contains(" / "+shot.Name+"："));
        }
        public static TxCamera Preview(IList<CameraShot> shots,CameraShot shot,double progress)
        {
            var range=Ranges(shots).FirstOrDefault(r=>ReferenceEquals(r.Shot,shot));
            if(range?.Start!=null&&shots.All(s=>!s.Enabled||s.Trigger!=ShotTrigger.Location))return new CameraShotScheduler(shots).Tick(range.Start.Value+progress*shot.Duration);
            return Prepare(shot,shots).Evaluate(progress);
        }
        public static List<string> Issues(IList<CameraShot> shots,double? duration,int locationCount)
        {
            var issues=new List<string>();
            if(!shots.Any(s=>s.Enabled))issues.Add("编排模式需要至少一个启用镜头；也可切换快速录像");
            foreach(var s in shots.Where(s=>s.Enabled))
            {
                var missing=Missing(s,shots);if(missing!=null)issues.Add(s.Name+"："+missing);
                if(s.Trigger==ShotTrigger.Location&&s.LocationIndex>locationCount)issues.Add(s.Name+"：Location 超出操作范围");
            }
            issues.AddRange(Conflicts(shots));
            try{CameraShotScheduler.Validate(shots);}catch(Exception ex){if(!issues.Contains(ex.Message))issues.Add(ex.Message);}
            if(duration.HasValue&&duration.Value>0)
                foreach(var range in Ranges(shots).Where(r=>r.Start.HasValue))
                    if(range.Start.Value+range.Shot.Duration>duration.Value+0.001)issues.Add(range.Shot.Name+"：超出操作预计时长");
            return issues.Distinct().ToList();
        }
        public static string Summary(IList<CameraShot> shots,double? duration)
        {
            var conflicts=Conflicts(shots);int total=shots.Count(s=>s.Enabled),ready=shots.Count(s=>s.Enabled&&Hint(s,shots,duration,int.MaxValue,conflicts)==null);
            return "镜头就绪 "+ready+"/"+total+(duration.HasValue?" · 操作约 "+duration.Value.ToString("0.###")+" s":" · 操作时长未知");
        }
    }
}
