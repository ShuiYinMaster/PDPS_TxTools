// ExportService.cs  —  C# 7.3
// 所有 PS API 调用通过 psCtx.Send() 路由回 PS 主线程

using System;
using System.Collections.Generic;
using System.Threading;

namespace TxTools.ExportGun
{
    public class ExportService : IDisposable
    {
        private readonly SynchronizationContext _psCtx;
        private          bool                   _disposed;

        public ExportService(SynchronizationContext psCtx)
        {
            // 基类 SynchronizationContext.Send 在调用线程内直接执行，不能用于 PS API。
            _psCtx = psCtx != null && psCtx.GetType() != typeof(SynchronizationContext)
                ? psCtx : new System.Windows.Forms.WindowsFormsSynchronizationContext();
        }

        // ── 公开：供外部路由到 PS 主线程 ─────────────────────────────
        public void InvokeOnPs(Action action) { OnPs(action); }

        // ── 公开：在后台线程执行（不阻塞UI，不需要PS线程）────────────
        public void InvokeOnBackground(Action action)
        {
            Thread t = new Thread(delegate()
            {
                try { action(); }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        // ── PS 主线程执行（同步等待）─────────────────────────────────
        private void OnPs(Action action)
        {
            Exception ex = null;
            _psCtx.Send(delegate(object s)
            {
                try { action(); } catch (Exception e) { ex = e; }
            }, null);
            if (ex != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();
        }

        private T OnPs<T>(Func<T> func)
        {
            T val = default(T); Exception ex = null;
            _psCtx.Send(delegate(object s)
            {
                try { val = func(); } catch (Exception e) { ex = e; }
            }, null);
            if (ex != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();
            return val;
        }

        // ── 加载操作列表 ─────────────────────────────────────────────
        public List<OperationInfo> LoadFromSelection(Action<string> log)
        {
            List<OperationInfo> ops = null;
            SafeLog(log, "[PS] 切换到 PS 主线程读取...");
            try
            {
                OnPs(delegate()
                {
                    ops = PsReader.GetOperationsFromSelection(log);
                });
            }
            catch (Exception ex)
            {
                SafeLog(log, "[错误] LoadFromSelection: " + ex.Message);
                ops = new List<OperationInfo>();
            }
            return ops ?? new List<OperationInfo>();
        }

        // ── 预览点数量 ────────────────────────────────────────────────
        public int PreviewPointCount(List<OperationInfo> ops,
                                      PointType pt, bool useMfg,
                                      Action<string> log)
        {
            int total = 0;
            try
            {
                OnPs(delegate()
                {
                    foreach (OperationInfo op in ops)
                    {
                        OperationInfo tmp = new OperationInfo
                            { Name = op.Name, PsObject = op.PsObject };
                        PsReader.FillPoints(tmp, pt, useMfg, Nop);
                        total += tmp.Points.Count;
                    }
                });
            }
            catch { }
            return total;
        }

        // ── 异步导出插枪 ─────────────────────────────────────────────
        public void ExportGunsAsync(GunExportParams p,
                                     Action<string> onLog,
                                     Action<ExportProgress> onProgress,
                                     Action<bool, string> onComplete)
        {
            RunCatiaExport(delegate ()
            {
                string stage = "PS 读取点数据";
                try
                {
                    // ── Step 1: 重新填充点（应用 PointFilter / UseMfgName 筛选） ─
                    //   注意：必须 Clear() 后 FillPoints，否则会沿用之前残留的全集，
                    //   导致点类型筛选不生效（之前的 bug）。
                    int totalPts = 0;
                    SafeLog(onLog, "[PS] 读取点数据（筛选：" + p.PointFilter
                        + "，UseMfgName=" + p.UseMfgName + "）...");
                    OnPs(delegate ()
                    {
                        foreach (OperationInfo op in p.Operations)
                        {
                            op.Points.Clear();
                            PsReader.FillPoints(op, p.PointFilter, p.UseMfgName, onLog);
                            totalPts += op.Points.Count;
                            SafeLog(onLog, "  [" + op.Name + "] " + op.Points.Count + " 个点");
                        }
                    });
                    SafeLog(onLog, "[PS] 合计 " + totalPts + " 个点");
                    if (totalPts == 0)
                    {
                        SafeComplete(onComplete, false,
                            "未找到符合条件的点。请检查点类型筛选与所选操作。");
                        return;
                    }

                    // ── Step 2: 读取焊钳信息 ─────────────────────────────
                    SafeLog(onLog, "[PS] 读取插枪信息...");
                    stage = "PS 读取插枪信息";
                    OnPs(delegate ()
                    {
                        if (p.GunOriginAtTCP && p.TcpCustomMatrix == null && !string.IsNullOrEmpty(p.TcpName))
                            p.ResolvedTcpMatrices = new Dictionary<string, double[]>();
                        foreach (OperationInfo op in p.Operations)
                        {
                            op.Gun = PsReader.GetGunFromOperation(
                                op, p.CustomModelPath, onLog);
                            if (p.ResolvedTcpMatrices != null && op.Gun != null)
                                p.ResolvedTcpMatrices[op.Name] = PsReader.ResolveTcpWorldByName(op, p.TcpName, onLog);
                            if (op.Gun == null)
                                SafeLog(onLog, "  ⚠ [" + op.Name + "] 无绑定 Robot");
                        }
                    });

                    // ── Step 3: 连接 CATIA 并导出 ─────────────────────────
                    using (var bridge = new CatiaBridge())
                    {
                        stage = "连接 CATIA";
                        Connect(bridge, onLog);
                        stage = "CATIA 插枪导出";
                        SafeLog(onLog, "[Catia] 开始导出插枪...");
                        bridge.ExportGuns(p,
                            delegate (ExportProgress pg) { SafeProgress(onProgress, pg); },
                            delegate (string msg) { SafeLog(onLog, msg); });
                        SafeComplete(onComplete, true, "插枪导出完成");
                    }
                }
                catch (Exception ex)
                { ReportFailure(new InvalidOperationException(stage + "：" + ex.Message, ex), onLog, onComplete); }
            });
        }

        // ── 异步导出点球 ─────────────────────────────────────────────
        public void ExportBallsAsync(BallExportParams       p,
                                      Action<string>         onLog,
                                      Action<ExportProgress> onProgress,
                                      Action<bool, string>   onComplete)
        {
            RunCatiaExport(delegate ()
            {
                string stage = "PS 读取点数据";
                try
                {
                    int totalPts = 0;
                    SafeLog(onLog, "[PS] 读取点数据...");
                    OnPs(delegate()
                    {
                        foreach (OperationInfo op in p.Operations)
                        {
                            op.Points.Clear();
                            PsReader.FillPoints(op, p.PointFilter, p.UseMfgName, onLog);
                            totalPts += op.Points.Count;
                            SafeLog(onLog, "  [" + op.Name + "] "
                                + op.Points.Count + " 个点");
                        }
                    });

                    if (totalPts == 0)
                    {
                        SafeComplete(onComplete, false,
                            "未找到符合条件的点。请检查：\n" +
                            "  1. 已勾选操作\n" +
                            "  2. 点类型选择正确\n" +
                            "  3. 操作确实包含对应点\n\n详情见日志");
                        return;
                    }
                    SafeLog(onLog, "[PS] 合计 " + totalPts + " 个点");

                    using (var bridge = new CatiaBridge())
                    {
                        stage = "连接 CATIA";
                        Connect(bridge, onLog);
                        stage = "CATIA 点球导出";
                        SafeLog(onLog, "[Catia] 开始创建点球...");
                        bridge.ExportBalls(p,
                            delegate(ExportProgress pg) { SafeProgress(onProgress, pg); },
                            delegate(string msg)         { SafeLog(onLog, msg); });
                        SafeComplete(onComplete, true,
                            "点球导出完成，共 " + totalPts + " 个点");
                    }
                }
                catch (Exception ex)
                { ReportFailure(new InvalidOperationException(stage + "：" + ex.Message, ex), onLog, onComplete); }
            });
        }

        // ── 导出 Excel（自动填充点，参考坐标由调用方传入）────────────
        // refMatrix / refName：用户在UI上已确认的参考坐标系
        //   null refMatrix = 世界坐标系（不转换）
        // 不在此处重新读PS选中状态（避免用户切换选中操作后坐标丢失）
        public void ExportExcelAsync(List<OperationInfo> ops,
                                      PointType           ptFilter,
                                      bool                useMfg,
                                      string              outputFolder,
                                      double[]            refMatrix,   // ← 新增
                                      string              refName,     // ← 新增
                                      Action<string>      onLog,
                                      Action<bool,string> onComplete)
        {
            ThreadPool.QueueUserWorkItem(delegate(object s)
            {
                try
                {
                    // Step 1：PS 主线程填充点（参考坐标已由调用方确定）
                    int totalPts = 0;

                    SafeLog(onLog, "[PS] 读取点数据...");
                    SafeLog(onLog, "[PS] 参考坐标：" + (refName ?? "世界坐标系"));
                    OnPs(delegate()
                    {
                        // 填充每个操作的点
                        foreach (OperationInfo op in ops)
                        {
                            op.Points.Clear();
                            PsReader.FillPoints(op, ptFilter, useMfg, onLog);
                            totalPts += op.Points.Count;
                            SafeLog(onLog, "  [" + op.Name + "] " + op.Points.Count + " 个点");
                        }
                    });

                    SafeLog(onLog, "[PS] 合计 " + totalPts + " 个点");
                    if (totalPts == 0)
                    {
                        SafeComplete(onComplete, false,
                            "未找到任何点数据。\n" +
                            "请检查操作类型和点类型过滤设置。");
                        return;
                    }

                    // Step 2：后台生成 Excel（无需 PS 线程）
                    string outFile = ExcelExporter.Export(ops, refMatrix, outputFolder,
                        delegate(string msg) { SafeLog(onLog, msg); });

                    if (outFile != null)
                        SafeComplete(onComplete, true, outFile);
                    else
                        SafeComplete(onComplete, false, "Excel 导出失败，请查看日志");
                }
                catch (Exception ex)
                {
                    SafeLog(onLog, "[错误] " + ex.Message);
                    SafeComplete(onComplete, false, ex.Message);
                }
            });
        }

        // ── CATIA 连接 ────────────────────────────────────────────────
        private static void Connect(CatiaBridge bridge, Action<string> log)
        {
            string err;
            if (bridge.Connect(out err))
            { SafeLog(log, "✓ 已连接 CATIA"); return; }
            SafeLog(log, "✗ " + err);
            throw new InvalidOperationException(err ?? "无法连接 CATIA", bridge.LastConnectionError);
        }

        // 每次任务在同一 STA 中连接、使用并释放 CATIA，避免跨任务复用 RCW。
        private void RunCatiaExport(Action work)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ExportService));
            var thread = new Thread(() => work()) { IsBackground = true, Name = "TxTools CATIA export" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        private static void ReportFailure(Exception ex, Action<string> onLog, Action<bool, string> onComplete)
        {
            Exception cause = ex;
            while (cause.InnerException != null) cause = cause.InnerException;
            string message = ex.Message + (cause != ex ? "\n" + cause.Message : "")
                + "\nHRESULT=0x" + cause.HResult.ToString("X8");
            SafeLog(onLog, "[错误] " + ex);
            SafeComplete(onComplete, false, message);
        }

        private static void SafeLog(Action<string> cb, string msg)
        { if (cb != null) try { cb(msg); } catch { } }
        private static void SafeProgress(Action<ExportProgress> cb, ExportProgress p)
        { if (cb != null) try { cb(p); } catch { } }
        private static void SafeComplete(Action<bool, string> cb, bool ok, string msg)
        { if (cb != null) try { cb(ok, msg); } catch { } }
        private static void Nop(string s) { }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }
    }
}
