using System;
using System.Collections.Generic;
using Tecnomatix.Engineering;
using Tecnomatix.Planning;
using Tecnomatix.Engineering.Utilities;
using Tecnomatix.Engineering.DataTypes;

namespace TxTools.Common
{
    /// <summary>PS 对象的运行时类型、规划类型、原型类型和设备分类；仅缓存本次读取。</summary>
    public static class SceneObjectTypes
    {
        // Labels follow the PS 2402 SDK type definitions. This table never adds absent scene types.
        private static readonly Dictionary<string, string> ObjectTypeLabels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Tx1DimensionalGeometry"] = "一维几何",
            ["Tx2Or3DimensionalGeometry"] = "二维或三维几何",
            ["TxComponent"] = "组件",
            ["TxCompoundPart"] = "复合零件",
            ["TxCompoundResource"] = "复合资源",
            ["TxDevice"] = "设备",
            ["TxGenericRoboticLocationOperation"] = "通用机器人位置操作",
            ["TxGeometry"] = "几何对象",
            ["TxGripper"] = "夹持器",
            ["TxGun"] = "焊枪",
            ["TxKinematicLink"] = "运动链节",
            ["TxNote"] = "注释",
            ["TxPartAppearance"] = "零件外观",
            ["TxRobot"] = "机器人",
            ["TxRoboticSeamLocationOperation"] = "机器人焊缝位置操作",
            ["TxRoboticViaLocationOperation"] = "机器人过渡位置操作",
            ["TxSeamMfgFeature"] = "焊缝制造特征",
            ["TxServoGun"] = "伺服焊枪",
            ["TxSweptVolume"] = "扫掠体",
            ["TxWeldLocationOperation"] = "焊点位置操作",
            ["TxWeldPoint"] = "焊点特征",
            ["TxGroup"] = "通用分组",
            ["TxTool"] = "工艺工具",
            ["TxFrame"] = "坐标系",
            ["TxSolid"] = "实体几何",
            ["TxSurface"] = "曲面几何"
        };

        public static string Label(Type type)
        {
            string label;
            if (ObjectTypeLabels.TryGetValue(type.Name, out label)) return label;
            return type.Name.StartsWith("Tx", StringComparison.Ordinal) ? type.Name.Substring(2) : type.Name;
        }


    }
        // One reader per request: shared prototypes are read once and never cached across studies.
        public sealed class SceneObjectTypeReader
        {
            private readonly Dictionary<string, TxTunePmDocumentPrototypeData> files = new Dictionary<string, TxTunePmDocumentPrototypeData>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, string> labels = new Dictionary<string, string>(StringComparer.Ordinal);

            public Dictionary<string, string> Read(ITxObject item)
            {
                var result = new Dictionary<string, string>(StringComparer.Ordinal);
                result[item.GetType().FullName] = SceneObjectTypes.Label(item.GetType());
                var model = item as ITxProcessModelObject;
                if (model == null) return result;
                ITxPlanningObject planning = null;
                try { planning = model.PlanningRepresentation; } catch { }
                if (planning == null) return result;
                try { Add(result, "planning:", planning.PlanningType, "资源类型"); } catch { }
                ITxPlanningPrototype prototype = planning as ITxPlanningPrototype;
                try { prototype = (planning as ITxPlanningInstance)?.Prototype ?? prototype; } catch { }
                if (prototype == null) return result;
                try { Add(result, "prototype:", prototype.PlanningType, "原型类型"); } catch { }
                try
                {
                    string path = prototype.FullPath;
                    if (string.IsNullOrWhiteSpace(path)) return result;
                    TxTunePmDocumentPrototypeData data;
                    if (!files.TryGetValue(path, out data))
                    {
                        files[path] = null;
                        data = new TxTunePmDocumentManager().LoadPrototypeDataFromDocument(path);
                        files[path] = data;
                    }
                    if (data != null)
                    {
                        Add(result, "prototype:", data.Type, "原型类型");
                        if (!string.IsNullOrWhiteSpace(data.Family)) result["family:" + data.Family] = "设备分类 · " + data.Family;
                    }
                }
                catch { } // Local prototype data may be absent on connected platforms.
                return result;
            }

            private void Add(Dictionary<string, string> result, string prefix, string type, string group)
            {
                if (string.IsNullOrWhiteSpace(type)) return;
                string label;
                if (!labels.TryGetValue(type, out label))
                {
                    label = type;
                    try { label = TxApplication.ActiveDocument.PlatformGlobalServicesProvider.GetTypeMetaData(type)?.DisplayName ?? type; } catch { }
                    labels[type] = label;
                }
                result[prefix + type] = group + " · " + label + (label == type ? "" : "（" + type + "）");
            }
        }


}
