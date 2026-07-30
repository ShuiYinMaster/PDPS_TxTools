// TxTools.Agent / Tools / Ps / SetViewToObjectTool.cs
// 把 PS 主视口聚焦到指定对象。
//
// 三个关键 API (从对话 conv_20260721 + AutoRecorder/PsReader 摸出):
//   - GetAllDescendants(null)                 → 拿所有后代 (传 TxTypeFilter(接口) 会不匹配返回空)
//   - TxApplication.ActiveSelection.SetItems(TxObjectList)  → 正确的选中 API
//   - TxApplication.CommandsManager.ExecuteCommand("GraphicViewer.ZoomToSelection")
//                                             → 关键: 命令 ID 带前缀 "GraphicViewer."
//   - viewer.ZoomToFit()                      → 兜底 (聚焦整个场景)
//
// 两种模式:
//   1. use_current_selection=true (或 object_name 为空) → 用当前 ActiveSelection 里的对象
//      场景: 前面 select_objects 已经选中,再调这个直接聚焦(避开同名歧义)
//   2. 按 object_name 找 → 同名的全部选中并聚焦 (整体最小外接框)
//
// 只读: 视角变化不进 undo 栈。

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Tecnomatix.Engineering;
using TxTools.Agent.Ps;

namespace TxTools.Agent.Core
{
    public sealed class SetViewToObjectTool : ITxAgentTool
    {
        public string Name { get { return "set_view_to_object"; } }
        public string Description
        {
            get
            {
                return "\u628a PS \u4e3b\u89c6\u53e3\u805a\u7126\u5230\u6307\u5b9a\u5bf9\u8c61(\u9009\u4e2d + Zoom-to-Selection \u547d\u4ee4)\u3002" +
                       "\u53c2\u6570 object_name (\u53ef\u9009,\u540c\u540d\u5219\u5168\u9009\u4e2d)\u3001" +
                       "object_id (\u53ef\u9009,\u573a\u666f\u552f\u4e00 ID,\u7ed9\u4e86\u5c31\u7cbe\u786e\u805a\u7126\u8fd9\u4e2a\u5b9e\u4f8b)\u3001" +
                       "use_current_selection (\u53ef\u9009,true=\u76f4\u63a5\u7528\u5f53\u524d ActiveSelection,\u907f\u514d\u540c\u540d\u6b67\u4e49)\u3002";
            }
        }
        public bool IsReadOnly { get { return true; } }

        public JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    'type': 'object',
                    'properties': {
                        'object_name':           { 'type': 'string', 'description': '按名字找; 同名对象全部选中并整体聚焦' },
                        'object_id':             { 'type': 'string', 'description': '对象的场景唯一 ID(形如 3,57,2,1)。同名对象只能用它精确聚焦；给了 object_id 就忽略 object_name' },
                        'use_current_selection': { 'type': 'boolean', 'default': false, 'description': 'true=直接用当前 ActiveSelection (先 select_objects 再本工具最实用)' }
                    }
                }");
            }
        }

        public string Execute(JObject input)
        {
            var name = ToolInputHelpers.String(input["object_name"]);
            var objectId = ToolInputHelpers.String(input["object_id"]);
            var useCurrentSel = ToolInputHelpers.Bool(input["use_current_selection"], false);

            var doc = TxApplication.ActiveDocument;
            if (doc == null) return "Error: \u65e0\u6d3b\u52a8\u6587\u6863";

            // 模式 0: 给了 object_id —— 直接按 ID 精确命中，跳过按名查找
            if (!string.IsNullOrWhiteSpace(objectId))
            {
                ITxObject o = null;
                try { o = doc.GetObjectById(objectId.Trim()); } catch { }
                if (o == null) return "Error: \u6309 ID \"" + objectId + "\" \u627e\u4e0d\u5230\u5bf9\u8c61\u3002";

                var list = new TxObjectList(1);
                list.Add(o);
                try { TxApplication.ActiveSelection.SetItems(list); }
                catch (Exception ex) { return "Error: \u9009\u4e2d\u5931\u8d25: " + ex.Message; }

                return DoZoomToCurrentSelection(PsBridge.Ref(o));
            }

            // 模式 1: 用当前 ActiveSelection
            if (useCurrentSel || string.IsNullOrWhiteSpace(name))
            {
                var sel = TxApplication.ActiveSelection;
                if (sel == null || sel.Count == 0)
                    return "Error: \u5f53\u524d\u65e0\u9009\u4e2d\u5bf9\u8c61,\u65e0\u6cd5\u805a\u7126\u3002\u5148 select_objects \u6216\u4f20 object_name\u3002";

                return DoZoomToCurrentSelection(sel.Count + " \u4e2a\u5df2\u9009\u4e2d\u5bf9\u8c61");
            }

            // 模式 2: 按 name 查找
            var targets = FindAllByName(doc.PhysicalRoot, name);
            if (targets.Count == 0) return "Error: \u627e\u4e0d\u5230\u5bf9\u8c61 " + name;

            // 保存原选中(万一操作失败可回滚)
            TxObjectList savedSel = null;
            try { savedSel = TxApplication.ActiveSelection.GetItems(); } catch { }

            try
            {
                var list = new TxObjectList(targets.Count);
                foreach (var t in targets) list.Add(t);
                TxApplication.ActiveSelection.SetItems(list);

                // 命中多个时列出各自的 名称 [Id]，方便用户/模型接着用 object_id 精确指定
                var labels = new string[targets.Count];
                for (int i = 0; i < targets.Count; i++) labels[i] = PsBridge.Ref(targets[i]);
                return DoZoomToCurrentSelection(name + " (\u5339\u914d " + targets.Count + " \u4e2a: "
                    + string.Join(", ", labels) + ")");
            }
            catch (Exception ex)
            {
                try { if (savedSel != null) TxApplication.ActiveSelection.SetItems(savedSel); } catch { }
                return "Error: " + ex.Message;
            }
        }

        // ── 内部 ──

        /// <summary>对当前 ActiveSelection 执行 Zoom to Selection。</summary>
        private static string DoZoomToCurrentSelection(string label)
        {
            string used = null;
            string err1 = null;
            try
            {
                TxApplication.CommandsManager.ExecuteCommand("GraphicViewer.ZoomToSelection");
                used = "GraphicViewer.ZoomToSelection";
            }
            catch (Exception ex1)
            {
                err1 = ex1.Message;
                // 兜底: viewer.ZoomToFit()
                try
                {
                    var v = TxApplication.ViewersManager.GraphicViewer;
                    if (v != null) { v.ZoomToFit(); used = "ZoomToFit(\u5168\u573a\u666f)"; }
                }
                catch (Exception ex2)
                {
                    return "Error: \u547d\u4ee4\u5931\u8d25 - " + err1 + " | \u5175\u5e95 ZoomToFit \u4e5f\u5931\u8d25 - " + ex2.Message;
                }
            }
            try { TxApplication.RefreshDisplay(); } catch { }

            if (used == null)
                return "\u5df2\u9009\u4e2d " + label + " \u4f46 zoom \u5931\u8d25;\u624b\u52a8\u6309 F \u952e\u5373\u53ef";
            return "\u5df2\u805a\u7126: " + label + "  [" + used + "]";
        }

        /// <summary>
        /// 按名字找所有匹配对象。用 dynamic GetAllDescendants(null)
        /// (AutoRecorder PsReader:63-77 的做法)，失败再用反射兜底。
        /// </summary>
        private static List<ITxObject> FindAllByName(object root, string name)
        {
            var result = new List<ITxObject>();
            if (root == null) return result;

            // 路径 A: dynamic GetAllDescendants(null) —— 最稳
            try
            {
                dynamic dp = root;
                var all = dp.GetAllDescendants(null) as System.Collections.IEnumerable;
                if (all != null)
                {
                    foreach (var item in all)
                    {
                        var o = item as ITxObject;
                        if (o == null) continue;
                        try
                        {
                            if (string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))
                                result.Add(o);
                        }
                        catch { }
                    }
                    if (result.Count > 0) return result;
                }
            }
            catch { }

            // 路径 B: 反射兜底 (GetAllDescendants 在具体类上, 接口/ITxCompound 未暴露)
            try
            {
                System.Reflection.MethodInfo method = null;
                foreach (var m in root.GetType().GetMethods(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    if (m.Name == "GetAllDescendants" && m.GetParameters().Length == 1)
                    {
                        method = m;
                        break;
                    }
                }
                if (method != null)
                {
                    var all = method.Invoke(root, new object[] { null }) as System.Collections.IEnumerable;
                    if (all != null)
                    {
                        foreach (var item in all)
                        {
                            var o = item as ITxObject;
                            if (o == null) continue;
                            try
                            {
                                if (string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))
                                    result.Add(o);
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }

            return result;
        }
    }
}