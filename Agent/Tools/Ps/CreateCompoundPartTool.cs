// TxTools.Agent / Tools / Ps / CreateCompoundPartTool.cs
// 在 PS Parts 树 (PhysicalRoot 或某个 CompoundPart) 下创建新的 TxCompoundPart。
// 变更工具,需审批。

using Newtonsoft.Json.Linq;
using Tecnomatix.Engineering;

namespace TxTools.Agent.Core
{
    public sealed class CreateCompoundPartTool : ITxAgentTool
    {
        public string Name { get { return "create_compound_part"; } }
        public string Description
        {
            get
            {
                return "\u5728 PS Parts \u6811\u4e0b\u521b\u5efa\u65b0\u7684 TxCompoundPart\u3002" +
                       "\u53c2\u6570 parent_name (\u53ef\u9009,\u9ed8\u8ba4 PhysicalRoot), " +
                       "type_name (\u5c55\u793a\u7c7b\u578b\u6807\u7b7e\uff0c\u4ec5\u5f53\u524d\u5e73\u53f0\u5df2\u6ce8\u518c\u7684\u7c7b\u578b\u4f1a\u5199\u5165 TypeName), " +
                       "name (\u53ef\u9009,\u521b\u5efa\u540e\u5c1d\u8bd5 rename)\u3002";
            }
        }
        public bool IsReadOnly { get { return false; } }

        public JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    'type': 'object',
                    'properties': {
                        'parent_id': { 'type': 'string', 'description': '父级唯一 ID，优先于名称' },
                         'parent_name': { 'type': 'string', 'description': '父对象名(留空 = PhysicalRoot)' },
                        'type_name':   { 'type': 'string', 'description': '类型标签,如 Assembly/SubAssembly/Component' },
                        'name':        { 'type': 'string', 'description': '期望的对象名(尽力 rename)' }
                    }
                }");
            }
        }

        public string Execute(JObject input)
        {
            var parentName = ToolInputHelpers.String(input["parent_name"]);
            var typeName = ToolInputHelpers.String(input["type_name"]);
            var desiredName = ToolInputHelpers.String(input["name"]);

            ITxObject parent;
            try { parent = PsCompoundHelper.ResolveParent(parentName, typeof(Tecnomatix.Engineering.ITxCompoundPartCreation), ToolInputHelpers.String(input["parent_id"])); }
            catch (System.Exception ex) { return "Error: \u627e\u4e0d\u5230\u7236\u5bf9\u8c61 - " + ex.Message; }

            TxCompoundPart cp;
            string pasteWarning = null;
            try
            {
                using (var undo = TxTools.Common.SceneUndoScope.Begin(TxApplication.ActiveDocument, "创建 CompoundPart"))
                {
                    cp = PsCompoundHelper.CreatePart(parent, typeName, desiredName);
                    // Create 不写原生树关系，单棵树也立即 Paste 一次，否则对象树平铺。
                    var collection = parent as ITxObjectCollection;
                    if (collection != null)
                    {
                        try
                        {
                            var copies = PsCompoundHelper.MoveToNativeTree(collection, new ITxObject[] { cp });
                            if (copies.Count > 0 && copies[0] is TxCompoundPart)
                            {
                                cp = (TxCompoundPart)copies[0];
                                if (!string.IsNullOrEmpty(desiredName)) PsCompoundHelper.TryRename(cp, desiredName);
                            }
                        }
                        catch (System.Exception pasteError)
                        {
                            pasteWarning = pasteError.GetBaseException().Message;
                        }
                    }
                }
            }
            catch (System.Exception ex) { return "Error: " + ex.Message; }

            var finalName = (cp as ITxObject).Name;
            var msg = "\u5df2\u521b\u5efa CompoundPart\n"
                + "  \u7236\u7ea7: " + (parent as ITxObject).Name + " (" + parent.GetType().Name + ")\n"
                + "  \u65b0\u5efa: " + finalName
                + (string.IsNullOrEmpty(typeName) ? "" : "  [TypeName 请求=" + typeName + "，仅当前平台注册的类型会写入]");
            if (pasteWarning != null)
                msg += "\n  \u26a0 原生树关系重建失败，对象树可能仍平铺: " + pasteWarning;
            if (!string.IsNullOrEmpty(desiredName) && finalName != desiredName)
                msg += "\n  \u26a0 rename \u6ca1\u751f\u6548,\u5b9e\u9645\u540d\u4e3a \"" + finalName + "\" (\u4e0d\u662f\u60f3\u8981\u7684 \"" + desiredName + "\")";
            return msg;
        }
    }
}
