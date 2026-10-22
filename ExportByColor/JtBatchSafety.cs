using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TxTools.ExportByColor
{
    internal static class JtBatchSafety
    {
        // Stop at a backing resource, but never at a prototype container. Identity,
        // not backing-file path, distinguishes independently placed instances.
        internal static List<T> Expand<T>(IEnumerable<T> roots, Func<T, string> key,
            Func<T, bool> container, Func<T, bool> hasBacking, Func<T, IEnumerable<T>> children,
            Action<T, Exception> onError = null) where T : class
        {
            var result = new List<T>();
            var seen = new HashSet<string>();
            var todo = new Stack<T>();
            foreach (var root in roots) if (root != null) todo.Push(root);
            while (todo.Count != 0)
            {
                var node = todo.Pop();
                if (node == null || !seen.Add(key(node))) continue;
                try
                {
                    if (!container(node) && hasBacking(node)) { result.Add(node); continue; }
                    bool added = false;
                    foreach (var child in children(node))
                        if (child != null && key(child) != key(node)) { todo.Push(child); added = true; }
                    // Keep an unresolved leaf so its error is counted without dropping
                    // healthy siblings. Empty prototypes must also produce a diagnostic.
                    if (!added) result.Add(node);
                }
                catch (Exception ex)
                {
                    result.Add(node);
                    if (onError != null) onError(node, ex);
                }
            }
            result.Reverse();
            return result;
        }

        internal static T TakeCompleted<T>(List<T> pending, Func<T, Task> task)
        {
            if (pending.Count == 0) throw new InvalidOperationException("Empty JT batch");
            var tasks = new Task[pending.Count];
            for (int i = 0; i < tasks.Length; i++) tasks[i] = task(pending[i]);
            // Await only the wrapper, so a faulted/cancelled resource is returned
            // to the per-resource error handler instead of faulting the batch.
            var completed = Task.WhenAny(tasks).GetAwaiter().GetResult();
            int index = Array.IndexOf(tasks, completed);
            var item = pending[index]; pending.RemoveAt(index); return item;
        }
    }
}
