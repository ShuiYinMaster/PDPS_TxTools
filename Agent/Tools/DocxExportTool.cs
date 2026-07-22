// TxTools.Agent / Tools / DocxExportTool.cs
// 让 agent 生成 Word (.docx) 报告文档 —— 输入 title + sections,内部走 OpenXmlWriter。
// 用法示例(agent 侧):
//   {
//     "file_name": "collision_report_2026-07-20",
//     "title": "碰撞检测报告",
//     "sections": [
//       { "heading": "概览", "paragraphs": ["场景中共发现 2 组碰撞集..."] },
//       { "heading": "详情", "table": [["组名","激活","首集对象"], ["...","是","..."]] }
//     ]
//   }

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TxTools.Agent.Core
{
    public sealed class DocxExportTool : ITxAgentTool
    {
        public string Name { get { return "export_docx"; } }
        public string Description
        {
            get
            {
                return "\u751f\u6210 Word (.docx) \u6587\u6863\u5230\u684c\u9762 TxTools_Exports \u76ee\u5f55\u3002" +
                       "\u8f93\u5165 file_name(\u4e0d\u542b\u6269\u5c55) + title + sections\u3002" +
                       "\u6bcf\u4e2a section: heading(\u53ef\u9009) + heading_level(1-3,\u9ed8\u8ba42) + " +
                       "paragraphs(\u5b57\u7b26\u4e32\u6570\u7ec4) + table(\u53ef\u9009,\u884c\u00d7\u5217,\u9996\u884c=\u8868\u5934)\u3002";
            }
        }
        public bool IsReadOnly { get { return false; } }   // 会创建文件,视为变更(会走审批)

        public JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    'type': 'object',
                    'required': ['sections'],
                    'properties': {
                        'file_name': { 'type': 'string', 'description': '文件名,不含扩展名,如 report_2026-07-20' },
                        'title':     { 'type': 'string', 'description': '文档大标题(可选)' },
                        'sections': {
                            'type': 'array',
                            'items': {
                                'type': 'object',
                                'properties': {
                                    'heading':        { 'type': 'string' },
                                    'heading_level':  { 'type': 'integer', 'default': 2, 'description': '1-3' },
                                    'paragraphs':     { 'type': 'array', 'items': { 'type': 'string' } },
                                    'table':          { 'type': 'array', 'items': { 'type': 'array', 'items': { 'type': 'string' } },
                                                        'description': '可选表格。行×列。首行会作为表头(加粗+灰底)。' }
                                }
                            }
                        }
                    }
                }");
            }
        }

        public string Execute(JObject input)
        {
            var fileName = ToolInputHelpers.String(input["file_name"]);
            var title = ToolInputHelpers.String(input["title"]);

            var sections = new List<DocxSection>();
            var sTok = input["sections"] as JArray;
            if (sTok != null)
            {
                foreach (var s in sTok)
                {
                    if (!(s is JObject so)) continue;
                    var sec = new DocxSection
                    {
                        Heading = ToolInputHelpers.String(so["heading"]),
                        HeadingLevel = ToolInputHelpers.Int(so["heading_level"], 2),
                        Paragraphs = ToolInputHelpers.StringList(so["paragraphs"])
                    };
                    var tTok = so["table"] as JArray;
                    if (tTok != null)
                    {
                        sec.Table = new List<List<string>>();
                        foreach (var row in tTok)
                        {
                            var ra = row as JArray;
                            if (ra == null) continue;
                            sec.Table.Add(ra.Select(c => ToolInputHelpers.String(c) ?? "").ToList());
                        }
                    }
                    sections.Add(sec);
                }
            }

            if (string.IsNullOrWhiteSpace(fileName))
                fileName = "document_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            if (!fileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
                fileName += ".docx";
            fileName = SafeFileName(fileName);

            var outDir = GetExportDir();
            var fullPath = Path.Combine(outDir, fileName);

            OpenXmlWriter.WriteDocx(fullPath, title, sections);

            var size = new FileInfo(fullPath).Length;
            return "\u5df2\u751f\u6210 Word \u6587\u6863: " + fullPath +
                   "  (" + FileParserService.FormatBytes(size) +
                   ", " + sections.Count + " \u4e2a\u7ae0\u8282)";
        }

        // ── 辅助 ──

        /// <summary>去掉文件名里 Windows 非法字符,防止 agent 传含 / \ : 等字符时崩。</summary>
        internal static string SafeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            foreach (var c in invalid) name = name.Replace(c, '_');
            return name;
        }

        /// <summary>默认输出到桌面 TxTools_Exports 目录,不存在则创建。</summary>
        internal static string GetExportDir()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "TxTools_Exports");
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }
    }
}
