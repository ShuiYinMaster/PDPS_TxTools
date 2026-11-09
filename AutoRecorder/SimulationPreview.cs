using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Tecnomatix.Engineering;

namespace TxTools.AutoRecorder
{
    /// <summary>SDK 回调只采集事件；相机和播放器操作串行运行在 UI 定时器中。</summary>
    internal sealed class SimulationPreview : IDisposable
    {
        private struct Destination {public double Time;public int Index;}
        private readonly object _gate=new object();
        private readonly Queue<Destination> _destinations=new Queue<Destination>();
        private readonly System.Windows.Forms.Timer _timer;
        private readonly int _thread;
        private readonly Stopwatch _clock=new Stopwatch();
        private TxSimulationPlayer _player;
        private TxGraphicViewer _viewer;
        private TxCamera _originalCamera,_lastCamera;
        private CameraShotScheduler _scheduler;
        private double _latestTime,_appliedTime;
        private long _lastProgress;
        private int _location;
        private bool _acceptEvents,_ended,_stopRequested,_pauseRequested,_inPump,_disposed,_started;
        private string _stopMessage;
        public bool Running {get {lock(_gate)return _player!=null;}}
        public bool Stopping {get {lock(_gate)return _stopRequested;}}
        public bool Paused {get;private set;}
        public bool CanRestoreView {get {return !Running&&!_disposed&&_originalCamera!=null;}}
        public event Action<double> Progress;
        public event Action StateChanged;
        public event Action<string> Finished;

        public SimulationPreview(SynchronizationContext context)
        {
            _thread=Thread.CurrentThread.ManagedThreadId;
            _timer=new System.Windows.Forms.Timer {Interval=33};
            _timer.Tick+=Pump;
        }
        private void RequireUi()
        {if(Thread.CurrentThread.ManagedThreadId!=_thread)throw new InvalidOperationException("预演控制必须在 PS 界面线程执行");}
        public void Start(ITxObject operation,OperationViewSchedule schedule,bool focus)
        {
            RequireUi();
            if(_disposed)throw new ObjectDisposedException(nameof(SimulationPreview));
            if(Running)throw new InvalidOperationException("已有预演尚未结束");
            if(TxApplication.ActiveDocument.SimulationPlayer.IsSimulationRunning())throw new InvalidOperationException("请先停止 PS 中正在播放的仿真");
            if(!(operation is ITxOperation))throw new InvalidOperationException("该对象不是可播放操作");
            _viewer=PsReader.GetGraphicViewer();_originalCamera=CameraShot.Snapshot(PsReader.GetCurrentCamera(_viewer));
            if(_viewer==null||_originalCamera==null)throw new InvalidOperationException("无法保存预演前视角");
            try
            {
                // 不改 CurrentOperation，不修改全局播放选项，也不在停止时回建仿真。
                var player=new TxSimulationPlayer();_player=player;
                player.SetOperation((ITxOperation)operation);
                _scheduler=schedule==null?null:new CameraShotScheduler(schedule.BuildShots());
                _latestTime=_appliedTime=0;_location=0;_lastProgress=-100;_lastCamera=null;
                _stopRequested=_pauseRequested=_ended=_started=false;Paused=false;
                player.TimeIntervalReached+=TimeReached;
                player.OperationReachedDestination+=LocationReached;
                player.Ended+=Ended;
                player.Rewind();
                if(schedule?.InitialCamera!=null)PsReader.SetCurrentCamera(_viewer,schedule.InitialCamera,false);
                else if(focus)PsReader.ZoomToObjects(PsReader.CollectOperationObjects(operation));
                ApplyCamera(_scheduler?.Tick(0));
                lock(_gate){_destinations.Clear();_ended=false;_acceptEvents=true;}
                _clock.Restart();_timer.Start();_started=true;player.Play();
            }
            catch
            {
                lock(_gate){_acceptEvents=false;_stopRequested=true;}
                FinishStop(false);throw;
            }
        }
        private void TimeReached(object sender,TxSimulationPlayer_TimeIntervalReachedEventArgs args)
        {
            double time=args.CurrentTime;
            if(double.IsNaN(time)||double.IsInfinity(time)||time<0)return;
            lock(_gate)
            {
                if(!_acceptEvents||!ReferenceEquals(sender,_player))return;
                _latestTime=Math.Max(_latestTime,time);
            }
        }
        private void LocationReached(object sender,EventArgs args)
        {
            lock(_gate)
            {
                if(!_acceptEvents||!ReferenceEquals(sender,_player))return;
                _destinations.Enqueue(new Destination {Time=_latestTime,Index=++_location});
            }
        }
        private void Ended(object sender,EventArgs args)
        {
            // 停止请求与自然结束可能同时到达，仍记录结束，避免对已结束的播放器再次 Stop。
            lock(_gate){if(ReferenceEquals(sender,_player))_ended=true;}
        }
        private void Pump(object sender,EventArgs args)
        {
            if(_inPump||_disposed)return;
            _inPump=true;
            try
            {
                Destination[] destinations;double time;bool stop,ended,pause;
                lock(_gate)
                {
                    if(_player==null)return;
                    stop=_stopRequested;ended=_ended;pause=_pauseRequested;_pauseRequested=false;
                    time=_latestTime;destinations=_destinations.ToArray();_destinations.Clear();
                }
                if(stop){FinishStop(true);return;}
                if(pause&&!ended)
                {
                    // 不持有事件锁调用 SDK，Pause/Play 可能同步或跨线程触发事件。
                    if(Paused){Paused=false;_player.Play();}else{_player.Pause();Paused=true;}
                    StateChanged?.Invoke();
                }
                TxCamera camera=null;
                foreach(var destination in destinations)
                {
                    _appliedTime=Math.Max(_appliedTime,destination.Time);
                    camera=_scheduler?.Tick(_appliedTime,destination.Index)??camera;
                }
                _appliedTime=Math.Max(_appliedTime,time);
                camera=_scheduler?.Tick(_appliedTime)??camera;
                ApplyCamera(camera);
                if(_clock.ElapsedMilliseconds-_lastProgress>=100)
                {_lastProgress=_clock.ElapsedMilliseconds;Progress?.Invoke(_appliedTime);}
                if(ended)
                {
                    lock(_gate){_acceptEvents=false;_stopRequested=true;}
                    _stopMessage="预演完成，保留当前仿真位置；可点击恢复视角";
                    FinishStop(true);
                }
            }
            catch(Exception ex)
            {
                lock(_gate){_acceptEvents=false;_stopRequested=true;}
                _stopMessage="预演失败："+ex.Message;
                FinishStop(true);
            }
            finally{_inPump=false;}
        }
        private void ApplyCamera(TxCamera camera)
        {
            if(camera==null||SameCamera(_lastCamera,camera))return;
            if(!PsReader.SetCurrentCamera(_viewer,camera,false))throw new InvalidOperationException("无法应用镜头");
            _lastCamera=CameraShot.Snapshot(camera);
        }
        private static bool SameCamera(TxCamera a,TxCamera b)
        {
            if(a==null||b==null)return false;
            return Near(a.Position,b.Position)&&Near(a.ReferencePoint,b.ReferencePoint)&&Near(a.UpVector,b.UpVector);
        }
        private static bool Near(TxVector a,TxVector b)
        {return Math.Abs(a.X-b.X)<1e-7&&Math.Abs(a.Y-b.Y)<1e-7&&Math.Abs(a.Z-b.Z)<1e-7;}
        public void TogglePause()
        {
            RequireUi();
            lock(_gate){if(_player==null||_stopRequested)return;_pauseRequested=!_pauseRequested;}
        }
        public void Stop(string message="已停止预演，保留当前仿真位置；可点击恢复视角")
        {
            RequireUi();
            lock(_gate)
            {
                if(_player==null||_stopRequested)return;
                _acceptEvents=false;_stopRequested=true;_stopMessage=message;
            }
            // 先返回按钮消息，下一次 UI 定时器只执行一次原生 Stop。
            _timer.Start();
            StateChanged?.Invoke();
        }
        private void FinishStop(bool notify)
        {
            _timer.Stop();
            TxSimulationPlayer player;bool ended;
            lock(_gate){player=_player;ended=_ended;_acceptEvents=false;}
            if(player==null)return;
            string error="";bool failed=false;
            try{if(_started&&!ended)player.Stop();}catch(Exception ex){failed=true;error="；停止失败："+ex.Message;}
            finally
            {
                try{player.TimeIntervalReached-=TimeReached;player.OperationReachedDestination-=LocationReached;player.Ended-=Ended;}
                catch(Exception ex){error+="；事件清理失败："+ex.Message;}
                lock(_gate){_player=failed&&!_disposed?player:null;_destinations.Clear();_stopRequested=_pauseRequested=false;}
                _scheduler=null;if(!failed)_started=false;Paused=false;_clock.Stop();
            }
            // 故意不 Rewind / ResetToDefaultSetting / JumpSimulationToTime，避免同步重算和 SDK 重入。
            if(notify&&!_disposed)Finished?.Invoke(failed?"原生停止失败，可重试停止；编辑保持锁定"+error:(_stopMessage??"预演已结束")+error);
        }
        public bool RestoreView()
        {
            RequireUi();
            if(Running||_originalCamera==null)return false;
            if(!PsReader.SetCurrentCamera(_viewer,_originalCamera))return false;
            _originalCamera=null;return true;
        }
        public void Dispose()
        {
            RequireUi();if(_disposed)return;_disposed=true;
            lock(_gate){_acceptEvents=false;_stopRequested=true;}
            FinishStop(false);_timer.Dispose();
        }
    }
}
