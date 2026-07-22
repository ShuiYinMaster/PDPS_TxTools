// TxTools.Agent / Tools / ExportTableTool.cs
// 把 agent 汇总好的表格数据导出为 .xlsx。配合 list_operations / count_objects /
// list_children / count_points 等信息工具：先汇总，再用本工具落地成 Excel。

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TxTools.Agent.Core;

namespace TxTools.Agent.Tools
{
    public sealed class ExportTableTool : TxAgentToolBase
    {
        public override string Name { get { return "export_table"; } }

        public override string Description
        {
            get
            {
                return "把表格数据导出为 Excel(.xlsx)。headers 是表头，rows 是每行的单元格数组(数量可与表头不同)。" +
                       "用它把你从场景汇总到的信息(设备清单、机器人清单、点数统计等)落地成 Excel 文件。" +
                       "返回保存路径。filename/sheet_name/folder 可选。";
            }
        }

        public override bool IsReadOnly { get { return true; } }   // 只写文件，不改场景

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""headers"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""表头列"" },
                        ""rows"": {
                            ""type"": ""array"",
                            ""description"": ""数据行，每行是一个单元格数组"",
                            ""items"": { ""type"": ""array"", ""items"": {} }
                        },
                        ""filename"": { ""type"": ""string"", ""description"": ""文件名(可省略, 默认带时间戳)"" },
                        ""sheet_name"": { ""type"": ""string"", ""description"": ""工作表名(可省略)"" },
                        ""folder"": { ""type"": ""string"", ""description"": ""输出目录(可省略, 默认 桌面\\TxAgentExport)"" }
                    },
                    ""required"": [""headers"", ""rows""]
                }");
            }
        }

        public override string Execute(JObject input)
        {
            try
            {
                var headers = ToStringList(input["headers"] as JArray);
                var rows = new List<IList<string>>();
                var rowsArr = input["rows"] as JArray;
                if (rowsArr != null)
                    foreach (var r in rowsArr)
                        rows.Add(ToStringList(r as JArray));

                if (headers.Count == 0 && rows.Count == 0)
                    return "没有可导出的数据(headers 和 rows 都为空)。";

                var folder = GetString(input, "folder", null);
                if (string.IsNullOrWhiteSpace(folder))
                    folder = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "TxAgentExport");

                var filename = GetString(input, "filename", null);
                if (string.IsNullOrWhiteSpace(filename))
                    filename = "TxAgent_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".xlsx";
                if (!filename.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) filename += ".xlsx";

                var sheet = GetString(input, "sheet_name", "Sheet1");
                var path = XlsxWriter.Write(Path.Combine(folder, filename), sheet, headers, rows);

                return "已导出 " + rows.Count + " 行到: " + path;
            }
            catch (Exception ex)
            {
                return "导出 Excel 失败: " + ex.Message;
            }
        }

        private static List<string> ToStringList(JArray arr)
        {
            var list = new List<string>();
            if (arr == null) return list;
            foreach (var t in arr)
            {
                if (t == null || t.Type == JTokenType.Null) list.Add("");
                else if (t.Type == JTokenType.String) list.Add((string)t);
                else list.Add(JsonConvert.SerializeObject(t)); // 数字/布尔等转文本，避开会崩的 ToString(Formatting)
            }
            return list;
        }
    }
}
