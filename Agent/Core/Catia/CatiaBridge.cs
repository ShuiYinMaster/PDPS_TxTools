// TxTools.Agent / Core / Catia / CatiaBridge.cs
// late-bound COM 连接 CATIA V5 —— 全反射版,不依赖 dynamic。
//
// 为什么不用 dynamic:
//   run_csharp 里的 CodeDom 编译器不引 Microsoft.CSharp.RuntimeBinder, 用不了 dynamic。
//   为保持 CatiaBridge 与 run_csharp 里的探测代码可以互换 (用户可能贴 run_csharp
//   snippet 复用其中的调用模式), 这里也走反射, 编程模型一致。
//
// COM 集合调用的关键坑 (对话 [140-142] 摸出来的):
//   Collection.Item(i) 在 CATIA COM 里必须用 BindingFlags.InvokeMethod,
//   如果用 GetProperty 会抛 "Exception has been thrown by the target of an invocation"。
//   Products / Documents / 所有 IDispatch 集合都遵守这个规则。

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace TxTools.Agent.Core.Catia
{
    public static class CatiaBridge
    {
        private const string ProgId = "CATIA.Application";

        private static object _app;

        /// <summary>拿 CATIA.Application COM 对象。找不到活跃实例则启动一个新的。</summary>
        public static object GetOrConnect()
        {
            if (_app != null)
            {
                // 简单探活
                try { InvokeGet(_app, "Name"); return _app; }
                catch { _app = null; }
            }

            try { _app = Marshal.GetActiveObject(ProgId); }
            catch (COMException) { _app = null; }

            if (_app != null) return _app;

            var t = Type.GetTypeFromProgID(ProgId);
            if (t == null)
                throw new InvalidOperationException(
                    "\u672a\u627e\u5230 CATIA V5 (ProgID=" + ProgId + ")\u3002" +
                    "\u786e\u8ba4 CATIA V5 \u5df2\u5b89\u88c5\u5e76\u80fd\u542f\u52a8\u3002");
            _app = Activator.CreateInstance(t);
            try { InvokeSet(_app, "Visible", true); } catch { }
            return _app;
        }

        // ── 反射三件套 (COM 属性 / 方法) ──

        /// <summary>反射调 COM 属性 Get。</summary>
        public static object InvokeGet(object target, string name)
        {
            return target.GetType().InvokeMember(name,
                BindingFlags.GetProperty, null, target, null);
        }

        /// <summary>反射调 COM 属性 Set。</summary>
        public static void InvokeSet(object target, string name, object value)
        {
            target.GetType().InvokeMember(name,
                BindingFlags.SetProperty, null, target, new[] { value });
        }

        /// <summary>反射调 COM 方法(含集合的 Item / 各种 GetXxx)。</summary>
        public static object InvokeMethod(object target, string name, params object[] args)
        {
            return target.GetType().InvokeMember(name,
                BindingFlags.InvokeMethod, null, target, args);
        }

        // ── 便捷诊断 ──

        public static string GetStatusText()
        {
            try
            {
                var app = GetOrConnect();
                string version = TryStr(() => InvokeGet(InvokeGet(app, "SystemService"), "Version"));
                string activeDoc = TryStr(() => InvokeGet(InvokeGet(app, "ActiveDocument"), "Name"))
                                   ?? "(\u65e0\u6d3b\u52a8\u6587\u6863)";
                return "\u5df2\u8fde\u63a5 CATIA V5\nVersion: " + (version ?? "?") + "\nActiveDocument: " + activeDoc;
            }
            catch (Exception ex)
            {
                return "\u8fde\u63a5\u5931\u8d25: " + ex.Message;
            }
        }

        internal static string TryStr(Func<object> f)
        {
            try { var v = f(); return v == null ? null : v.ToString(); }
            catch { return null; }
        }

        internal static int TryInt(Func<object> f, int fallback = 0)
        {
            try { var v = f(); if (v == null) return fallback; return Convert.ToInt32(v); }
            catch { return fallback; }
        }
    }

    /// <summary>CATIA 树节点(反序列化契约不变,兼容原 catia_read_tree)。</summary>
    public sealed class CatiaProductNode
    {
        public string Name { get; set; }
        public string PartNumber { get; set; }
        public string Revision { get; set; }
        public string Definition { get; set; }
        public bool IsAssembly { get; set; }
        public List<CatiaProductNode> Children { get; set; } = new List<CatiaProductNode>();

        public int TotalDescendantCount()
        {
            int n = Children.Count;
            foreach (var c in Children) n += c.TotalDescendantCount();
            return n;
        }
    }

    public static class CatiaTreeReader
    {
        public static CatiaProductNode ReadActiveTree(int maxDepth = 20)
        {
            var app = CatiaBridge.GetOrConnect();
            var doc = CatiaBridge.InvokeGet(app, "ActiveDocument");
            if (doc == null)
                throw new InvalidOperationException("CATIA \u65e0\u6d3b\u52a8\u6587\u6863\u3002");

            object product;
            try { product = CatiaBridge.InvokeGet(doc, "Product"); }
            catch
            {
                throw new InvalidOperationException(
                    "\u5f53\u524d\u6587\u6863\u4e0d\u662f Product(\u65e0 .Product \u5c5e\u6027)\u3002" +
                    "\u8bf7\u5728 CATIA \u91cc\u6253\u5f00\u4e00\u4e2a .CATProduct \u6587\u4ef6\u3002");
            }
            if (product == null)
                throw new InvalidOperationException("\u6587\u6863 .Product \u4e3a null\u3002");

            return ReadRecursive(product, 0, maxDepth);
        }

        private static CatiaProductNode ReadRecursive(object prod, int depth, int maxDepth)
        {
            var node = new CatiaProductNode
            {
                Name = CatiaBridge.TryStr(() => CatiaBridge.InvokeGet(prod, "Name")),
                PartNumber = CatiaBridge.TryStr(() => CatiaBridge.InvokeGet(prod, "PartNumber")),
                Revision = CatiaBridge.TryStr(() => CatiaBridge.InvokeGet(prod, "Revision")),
                Definition = CatiaBridge.TryStr(() => CatiaBridge.InvokeGet(prod, "Definition"))
            };

            if (depth >= maxDepth) return node;

            try
            {
                var subs = CatiaBridge.InvokeGet(prod, "Products");
                if (subs == null) return node;

                var count = CatiaBridge.TryInt(() => CatiaBridge.InvokeGet(subs, "Count"));
                node.IsAssembly = (count > 0);

                for (int i = 1; i <= count; i++)
                {
                    try
                    {
                        // 关键: Item 必须用 InvokeMethod,不是 GetProperty (对话摸出)
                        var child = CatiaBridge.InvokeMethod(subs, "Item", i);
                        if (child != null)
                            node.Children.Add(ReadRecursive(child, depth + 1, maxDepth));
                    }
                    catch { /* 跳过读不了的 */ }
                }
            }
            catch { }

            return node;
        }
    }
}