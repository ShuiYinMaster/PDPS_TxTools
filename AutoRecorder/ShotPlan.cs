using System;
using System.Collections.Generic;
using System.Linq;

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
        public static string Missing(CameraShot shot)
        {
            if(!shot.Enabled)return "已停用";
            if((shot.Kind==ShotKind.Orbit||shot.Kind==ShotKind.Dolly)&&
                (shot.Focus==null||(shot.FocusSource>0&&shot.FocusObject==null)))return "待选焦点";
            if(shot.From==null)return "待记录起点";
            if(shot.Kind==ShotKind.Move&&shot.To==null)return "待记录终点";
            try{shot.Evaluate(0);shot.Evaluate(.5);shot.Evaluate(1);}catch(Exception ex){return ex.Message;}
            return null;
        }
        public static List<string> Issues(IList<CameraShot> shots,double? duration,int locationCount)
        {
            var issues=new List<string>();
            if(!shots.Any(s=>s.Enabled))issues.Add("编排模式需要至少一个启用镜头；也可切换快速录像");
            foreach(var s in shots.Where(s=>s.Enabled))
            {
                var missing=Missing(s);if(missing!=null)issues.Add(s.Name+"："+missing);
                if(s.Trigger==ShotTrigger.Location&&s.LocationIndex>locationCount)issues.Add(s.Name+"：Location 超出操作范围");
            }
            try{CameraShotScheduler.Validate(shots);}catch(Exception ex){if(!issues.Contains(ex.Message))issues.Add(ex.Message);}
            if(duration.HasValue&&duration.Value>0)
                foreach(var range in Ranges(shots).Where(r=>r.Start.HasValue))
                    if(range.Start.Value+range.Shot.Duration>duration.Value+0.001)issues.Add(range.Shot.Name+"：超出操作预计时长");
            return issues.Distinct().ToList();
        }
        public static string Summary(IList<CameraShot> shots,double? duration)
        {
            int total=shots.Count(s=>s.Enabled),ready=shots.Count(s=>s.Enabled&&Missing(s)==null);
            return "镜头就绪 "+ready+"/"+total+(duration.HasValue?" · 操作约 "+duration.Value.ToString("0.###")+" s":" · 操作时长未知");
        }
    }
}
