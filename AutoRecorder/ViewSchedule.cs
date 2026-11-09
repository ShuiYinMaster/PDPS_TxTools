using System;
using System.Collections.Generic;
using Tecnomatix.Engineering;
namespace TxTools.AutoRecorder
{
    /// <summary>关键帧：到达第 N 个 location 时切换到指定视角</summary>
    public class CameraKeyframe
    {
        public int LocationIndex;   // 1-based：到达第 N 个 location 触发
        public TxCamera Camera;
    }

    /// <summary>一个 operation 的视角脚本</summary>
    public class OperationViewSchedule
    {
        public bool Quick;
        /// <summary>op 开播时套用（对应 LocationIndex=0）。null = 不强制起始视角</summary>
        public TxCamera InitialCamera;
        /// <summary>关键帧列表（推荐按 LocationIndex 升序）</summary>
        public List<CameraKeyframe> Keyframes = new List<CameraKeyframe>();
        public List<CameraShot> Shots = new List<CameraShot>();

        internal List<CameraShot> BuildShots()
        {
            var combined=new List<CameraShot>(Shots);
            foreach(var key in Keyframes)
                if(key!=null&&key.Camera!=null)combined.Add(new CameraShot {Name="Location #"+key.LocationIndex,Trigger=ShotTrigger.Location,LocationIndex=key.LocationIndex,From=CameraShot.Snapshot(key.Camera),Duration=0.001,IsLegacy=true});
            return combined;
        }
        public bool IsEmpty
        {
            get { return InitialCamera == null && (Keyframes == null || Keyframes.Count == 0) && Shots.Count == 0; }
        }
    }

}
