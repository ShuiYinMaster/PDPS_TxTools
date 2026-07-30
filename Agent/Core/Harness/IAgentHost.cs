using System;

namespace TxAgent.Core
{
    /// <summary>宿主运行模式，决定安全策略走哪条路。</summary>
    public enum HostMode
    {
        Unknown = 0,

        /// <summary>本地文件模式。回滚 = 保存前一版本（PDPS 保存会新建文件，旧文件进回收站）。</summary>
        Standalone = 1,

        /// <summary>eMServer / Teamcenter 在线模式。回滚 = Undo Checkout。</summary>
        Connected = 2
    }

    /// <summary>一次回滚点的记录，用于出问题时告诉用户怎么回退。</summary>
    public sealed class RestorePoint
    {
        /// <summary>是否真的建立了可用的回滚点。false 表示宿主没能提供保护。</summary>
        public bool Created { get; set; }

        public HostMode Mode { get; set; }

        public DateTime TimeUtc { get; set; }

        /// <summary>Standalone 模式下的备份文件路径；Connected 模式下可留空。</summary>
        public string FilePath { get; set; }

        /// <summary>给用户看的一句话回滚说明。</summary>
        public string HowToRollback { get; set; }

        public static RestorePoint None(string reason)
        {
            return new RestorePoint
            {
                Created = false,
                TimeUtc = DateTime.UtcNow,
                HowToRollback = reason
            };
        }
    }

    /// <summary>
    /// 宿主能力抽象。TxAgent 实现 PS 版，CatiaAgent 实现 CATIA 版，Core 本身对二者都无感知。
    /// </summary>
    public interface IAgentHost
    {
        HostMode Mode { get; }

        /// <summary>把委托封送到宿主主线程执行（PS 侧走 SynchronizationContext.Send）。</summary>
        void Invoke(Action action);

        /// <summary>带返回值的主线程封送。</summary>
        T Invoke<T>(Func<T> func);

        /// <summary>
        /// 请求用户确认。destructive=true 时 UI 应做更强提示。
        /// 返回 false 表示用户否决，Core 会把否决结果回灌给模型。
        /// </summary>
        bool Confirm(string title, string detail, bool destructive);

        /// <summary>
        /// 建立回滚点。Standalone 下保存工程并记录旧文件位置；
        /// Connected 下记录签出基线（或直接返回 None 让 Core 退化为逐次确认）。
        /// 实现方不应抛异常，失败时返回 RestorePoint.None。
        /// </summary>
        RestorePoint CreateRestorePoint(string reason);

        /// <summary>日志。level 建议 "debug" / "info" / "warn" / "error"。</summary>
        void Log(string level, string message);
    }
}
