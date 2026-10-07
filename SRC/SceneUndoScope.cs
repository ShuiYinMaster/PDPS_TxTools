using System;
using System.Collections.Generic;
using System.Threading;
using Tecnomatix.Engineering;

namespace TxTools.Common
{
    /// <summary>
    /// PS 场景的撤销分组。必须在主线程使用；仅分组，不提供自动回滚或文件恢复。
    /// 嵌套插件调用共享外层事务；关闭失败后阻止同一文档上的后续写入。
    /// </summary>
    public sealed class SceneUndoScope : IDisposable
    {
        private sealed class Transaction
        {
            internal TxDocument Document;
            internal TxUndoTransactionManager Manager;
            internal int Depth;
            internal bool CloseFailed;
        }

        [ThreadStatic] private static Transaction _current;
        [ThreadStatic] private static HashSet<TxDocument> _failedDocuments;
        /// <summary>用于阻止事务执行期间保存/重载工程。</summary>
        public static bool HasActiveTransaction { get { return _current != null && _current.Depth > 0; } }
        /// <summary>循环内放行 UI 消息后，禁止继续写入已切走的工程。</summary>
        public static void EnsureActiveDocument()
        {
            if (_current != null && !Equals(_current.Document, TxApplication.ActiveDocument))
                throw new InvalidOperationException("事务执行期间工程已切换，已停止场景变更。");
        }
        private readonly Transaction _transaction;
        private readonly int _depth;
        private readonly int _thread;
        private bool _disposed;

        private SceneUndoScope(Transaction transaction)
        {
            _transaction = transaction;
            _depth = ++transaction.Depth;
            _thread = Thread.CurrentThread.ManagedThreadId;
        }

        /// <summary>开启一次撤销分组。无法开启时抛异常，调用方不得继续写场景。</summary>
        public static SceneUndoScope Begin(string label)
        {
            return Begin(TxApplication.ActiveDocument, label);
        }

        /// <summary>绑定确切文档和管理器，避免结束时操作另一个文档。</summary>
        public static SceneUndoScope Begin(TxDocument document, string label)
        {
            if (document == null || !Equals(document, TxApplication.ActiveDocument))
                throw new InvalidOperationException("没有有效的活动工程，已取消场景变更：" + label);
            if (_failedDocuments != null && _failedDocuments.Contains(document))
                throw new InvalidOperationException("上次撤销事务关闭失败，请重新打开工程后再执行写操作。");
            if (_current != null)
            {
                if (!Equals(_current.Document, document))
                {
                    if (_current.Depth != 0)
                        throw new InvalidOperationException("事务执行期间工程已切换，已取消变更。");
                    _current = null;
                }
                else if (_current.CloseFailed)
                    throw new InvalidOperationException("上次撤销事务关闭失败，请重新打开工程后再执行写操作。");
            }
            if (_current == null)
            {
                var manager = document.UndoManager;
                if (manager == null)
                    throw new InvalidOperationException("没有可用的撤销管理器，已取消场景变更：" + label);
                // PS 2402 经 DLL/XML 验证的真实签名；开始失败不留下伪成功状态。
                manager.StartTransaction();
                _current = new Transaction { Document = document, Manager = manager };
            }
            return new SceneUndoScope(_current);
        }

        /// <summary>每次可能放行 UI 消息的循环写入前检查工程未切换。</summary>
        public void EnsureDocument()
        {
            if (_disposed || !Equals(_transaction.Document, TxApplication.ActiveDocument))
                throw new InvalidOperationException("工程已切换或事务已结束，已停止场景变更。");
        }

        /// <summary>只关闭本工具成功开启的事务，不执行 Undo 或清空宿主历史。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            if (_thread != Thread.CurrentThread.ManagedThreadId || _transaction.Depth != _depth)
                throw new InvalidOperationException("撤销事务必须在原线程按嵌套顺序结束。");
            _disposed = true;
            if (--_transaction.Depth != 0) return;
            try
            {
                _transaction.Manager.EndTransaction();
                _current = null;
            }
            catch (Exception ex)
            {
                _transaction.CloseFailed = true;
                if (_failedDocuments == null) _failedDocuments = new HashSet<TxDocument>();
                _failedDocuments.Add(_transaction.Document);
                throw new InvalidOperationException("撤销事务关闭失败；场景可能已变更，请重新打开工程：" + ex.Message, ex);
            }
        }
    }
}
