// TxTools.Agent / Tools / ActionTools.cs
// 把信息工具和操作工具串起来的动作原语。

using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using TxTools.Agent.Core;
using TxTools.Agent.Ps;

namespace TxTools.Agent.Tools
{
    /// <summary>按名称选中场景对象，打通"查到 -> 选中 -> 用基于选择的工具操作"。</summary>
    public sealed class SelectObjectsTool : TxAgentToolBase
    {
        public override string Name { get { return "select_objects"; } }

        public override string Description
        {
            get
            {
                return "按名称在场景里查找并设为当前选中(替换原选择)。用于把 count_objects/list_children 查到的对象" +
                       "选起来，再交给基于选择的工具(如 count_points / export_points_excel / align_devices_z)。";
            }
        }

        // 仅改变当前选择(非破坏、可由用户点击还原)，免审批。
        public override bool IsReadOnly { get { return true; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""names"": {
                            ""type"": ""array"",
                            ""items"": { ""type"": ""string"" },
                            ""description"": ""要选中的对象名列表(精确优先，找不到再模糊匹配)""
                        },
                        ""object_ids"": {
                            ""type"": ""array"",
                            ""items"": { ""type"": ""string"" },
                            ""description"": ""对象场景唯一 ID 数组。与 names 二选一；同名对象只能用它精确选中。""
                        }
                    },
                    ""required"": [""names""]
                }");
            }
        }

        public override string Execute(JObject input)
        {
            var names = new List<string>();
            var arr = input != null ? input["names"] as JArray : null;
            if (arr != null)
                foreach (var t in arr)
                    if (t != null && t.Type == JTokenType.String) names.Add((string)t);

            var objectIds = new List<string>();
            var arrIds = input != null ? input["object_ids"] as JArray : null;
            if (arrIds != null)
                foreach (var t in arrIds)
                    if (t != null && t.Type == JTokenType.String) objectIds.Add((string)t);

            return PsBridge.SelectObjects(names, objectIds);
        }
    }

    /// <summary>把当前选中操作的焊点/路径点坐标导出为 Excel(复用 ExcelExporter，含参考系转换)。</summary>
    public sealed class ExportPointsExcelTool : TxAgentToolBase
    {
        public override string Name { get { return "export_points_excel"; } }

        public override string Description
        {
            get
            {
                return "把当前选中操作里的点坐标导出为 Excel(.xlsx)，按当前参考坐标系做欧拉角/坐标转换。" +
                       "point_type 可选 WeldPoint/PathPoint/ContinuousPoint/All。这是 ExportGun 同款的真实坐标导出。";
            }
        }

        public override bool IsReadOnly { get { return true; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""point_type"": {
                            ""type"": ""string"",
                            ""enum"": [""WeldPoint"", ""PathPoint"", ""ContinuousPoint"", ""All""],
                            ""description"": ""点类型，默认 All""
                        },
                        ""use_mfg_name"": { ""type"": ""boolean"", ""description"": ""是否按制造特征名读取，默认 false"" },
                        ""folder"": { ""type"": ""string"", ""description"": ""输出目录(可省略，默认 桌面\\CatiaExport)"" }
                    }
                }");
            }
        }

        public override string Execute(JObject input)
        {
            var pointType = GetString(input, "point_type", "All");
            var folder = GetString(input, "folder", null);
            bool useMfg = false;
            var t = input != null ? input["use_mfg_name"] : null;
            if (t != null && t.Type == JTokenType.Boolean) useMfg = (bool)t;
            return PsBridge.ExportPointsExcel(pointType, useMfg, folder);
        }
    }

    /// <summary>遍历场景并把匹配类型的对象清单一步导出为 Excel(真实数据流)。</summary>
    public sealed class ExportObjectListTool : TxAgentToolBase
    {
        public override string Name { get { return "export_object_list"; } }

        public override string Description
        {
            get
            {
                return "遍历场景，把匹配 type_keyword 的对象(名称/类型/父级)一步导出为 Excel。" +
                       "例如 type_keyword=\"Robot\" 导出机器人清单，留空导出全部对象。这是带真实数据流的一步式导出。";
            }
        }

        public override bool IsReadOnly { get { return true; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""type_keyword"": { ""type"": ""string"", ""description"": ""类型名关键字(模糊)，留空导出全部"" },
                        ""folder"": { ""type"": ""string"", ""description"": ""输出目录(可省略，默认 桌面\\TxAgentExport)"" }
                    }
                }");
            }
        }

        public override string Execute(JObject input)
        {
            return PsBridge.ExportObjectList(GetString(input, "type_keyword", null), GetString(input, "folder", null));
        }
    }
}
