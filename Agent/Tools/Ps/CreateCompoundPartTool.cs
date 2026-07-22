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
                       "type_name (\u5c55\u793a\u7c7b\u578b\u6807\u7b7e), " +
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
            try { parent = PsCompoundHelper.ResolveParent(parentName, typeof(Tecnomatix.Engineering.ITxCompoundPartCreation)); }
            catch (System.Exception ex) { return "Error: \u627e\u4e0d\u5230\u7236\u5bf9\u8c61 - " + ex.Message; }

            TxCompoundPart cp;
            try { cp = PsCompoundHelper.CreatePart(parent, typeName, desiredName); }
            catch (System.Exception ex) { return "Error: " + ex.Message; }

            var finalName = (cp as ITxObject).Name;
            var msg = "\u5df2\u521b\u5efa CompoundPart\n"
                + "  \u7236\u7ea7: " + (parent as ITxObject).Name + " (" + parent.GetType().Name + ")\n"
                + "  \u65b0\u5efa: " + finalName
                + (string.IsNullOrEmpty(typeName) ? "" : "  [TypeName=" + typeName + "]");
            if (!string.IsNullOrEmpty(desiredName) && finalName != desiredName)
                msg += "\n  \u26a0 rename \u6ca1\u751f\u6548,\u5b9e\u9645\u540d\u4e3a \"" + finalName + "\" (\u4e0d\u662f\u60f3\u8981\u7684 \"" + desiredName + "\")";
            return msg;
        }
    }
}
