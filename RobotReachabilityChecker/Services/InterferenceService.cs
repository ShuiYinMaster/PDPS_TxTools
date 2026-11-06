using System;
using Tecnomatix.Engineering;
using TxTools.AutoPathPlanner;
using TxTools.RobotReachabilityChecker.Diagnostics;

namespace TxTools.RobotReachabilityChecker.Services
{
    /// <summary>窗体持有的单操作缓存，场景变化时重建，关闭时释放。</summary>
    public sealed class InterferenceSession : IDisposable
    {
        private InterferenceContext _context;
        private TxDocument _document;
        private string _key, _fingerprint;

        public InterferenceContext Acquire(ITxObject operation, TxRobot robot, TxDocument doc,
            ILogger log, Action<string> stage)
        {
            stage?.Invoke("核对干涉范围缓存");
            string fingerprint = null;
            try { fingerprint = CollisionSetService.ScopeFingerprint(robot); }
            catch { /* 无法确认场景未变时重新准备，避免复用过期范围。 */ }
            string key = operation.Id + ":" + robot.Id;
            if (_context != null && _context.Ready && _document == doc && _key == key &&
                fingerprint != null && fingerprint == _fingerprint)
            {
                stage?.Invoke("复用窗口内干涉范围");
                return _context;
            }
            Dispose();
            _context = InterferenceService.Prepare(robot, doc, log, stage);
            _document = doc; _key = key; _fingerprint = fingerprint;
            return _context;
        }

        public void Dispose()
        {
            _context?.Dispose(); _context = null;
            _document = null; _key = null; _fingerprint = null;
        }
    }

    /// <summary>路径检查期间复用的临时干涉范围；不写入场景碰撞对。</summary>
    public sealed class InterferenceContext : IDisposable
    {
        internal CollisionSetService Scope { get; set; }
        public bool Ready => Scope != null;
        public string LastCollisionDetail { get; internal set; }

        public void Dispose()
        {
            Scope?.Dispose();
            Scope = null;
        }
    }

    public static class InterferenceService
    {
        public static InterferenceContext Prepare(TxRobot robot, TxDocument doc, ILogger log = null,
            Action<string> stage = null)
        {
            log = log ?? NullLogger.Instance;
            if (robot == null || doc == null) return null;
            try
            {
                if (doc != TxApplication.ActiveDocument)
                    throw new InvalidOperationException("干涉检查文档不是当前活动文档");
                var scope = CollisionSetService.CreateTemporaryRobotVsWorld(robot, text => log.Log(text), stage);
                log.Log($"  临时干涉范围: 检测方 {scope.CheckObjectCount} 项，障碍方 {scope.ObstacleObjectCount} 项");
                return new InterferenceContext { Scope = scope };
            }
            catch (Exception ex)
            {
                log.Log("  临时干涉范围准备失败: " + ex.Message, "WARN");
                return null;
            }
        }

        /// <summary>true=实际干涉，false=无干涉，null=检查未知。</summary>
        public static bool? CheckCollisionAtCurrentPose(InterferenceContext ctx, ILogger log = null)
        {
            if (ctx == null || !ctx.Ready) return null;
            string detail;
            bool? hit = ctx.Scope.QueryScopedCollision(out detail);
            ctx.LastCollisionDetail = detail;
            return hit;
        }
    }
}
