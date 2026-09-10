// CrossEnvRebuildTool.cs  --  C# 8.0
// 跨环境远程重建工具：在【被调用的 PS 实例】里执行结构/零件/焊点重建。
//
// 目的：主控端「复制 cojt 到对端库」之后，不必再手动切到对端窗口点重建 ——
// 通过 RPC 直接在对端进程里调用本工具完成重建，实现一键「推送到对端并重建」。
//
// 注意：
//   • 本工具修改场景 → IsReadOnly=false（跨环境写已放开，主被控互访）。
//   • 依赖 PsRpcServer 把 Execute 封送回 PS 主线程执行，场景线程安全。
//   • 由于 TxAgentCommand.AutoRegisterTools 扫描全程序集的 ITxAgentTool
//     并自动注册（要求 public 无参构造），本工具在每个 PS 实例都会注册。

using System;
using System.Text;
using Newtonsoft.Json.Linq;
using Tecnomatix.Engineering;
using TxTools.Agent.Core;

namespace TxTools.CrossEnvIO
{
    public sealed class CrossEnvRebuildTool : TxAgentToolBase
    {
        public override string Name { get { return "crossenv_rebuild"; } }

        public override string Description
        {
            get
            {
                return "在【当前 PS 实例】里执行跨环境重建（供对端通过 RPC 调用）："
                     + "kind=resource|part|weld；file 为 TSV 数据文件路径。"
                     + "resource/part 重建目录结构+位姿，weld 重建焊点（可绑定/投影）。"
                     + "由跨环境 IO 插件一键推送时自动调用，一般无需模型直接使用。";
            }
        }

        /// <summary>会修改场景 → 非只读。跨环境写已放开，可在对端进程内执行。</summary>
        public override bool IsReadOnly { get { return false; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    'type': 'object',
                    'properties': {
                        'kind': { 'type': 'string', 'description': 'resource | part | weld' },
                        'file': { 'type': 'string', 'description': 'TSV 数据文件绝对路径' },
                        'project': { 'type': 'boolean', 'description': 'weld: 重建后投影到零件表面' },
                        'bind_only': { 'type': 'boolean', 'description': 'weld: 仅绑定已有焊点，不新建' },
                        'origin_x': { 'type': 'number', 'description': '重建原点 X（世界坐标偏移，用于场景坐标同步）' },
                        'origin_y': { 'type': 'number', 'description': '重建原点 Y' },
                        'origin_z': { 'type': 'number', 'description': '重建原点 Z' }
                    },
                    'required': ['kind', 'file']
                }");
            }
        }

        public override string Execute(JObject input)
        {
            var kind = (GetString(input, "kind") ?? "").Trim().ToLowerInvariant();
            var file = GetString(input, "file");
            if (string.IsNullOrWhiteSpace(kind)) return "Error: 缺少 kind。";
            if (string.IsNullOrWhiteSpace(file)) return "Error: 缺少 file。";

            var doc = TxApplication.ActiveDocument;
            if (doc == null) return "Error: 当前实例没有打开的工程。";

            var sb = new StringBuilder();
            Action<string> log = s => { lock (sb) { sb.AppendLine(s); } };

            try
            {
                switch (kind)
                {
                    case "resource":
                    case "part":
                    {
                        double ox = NumVal(input, "origin_x");
                        double oy = NumVal(input, "origin_y");
                        double oz = NumVal(input, "origin_z");
                        var rep = StructureIO.RebuildStructure(file, doc.PhysicalRoot,
                            kind == "resource", log, ox, oy, oz);
                        foreach (var e in rep.Errors) log("[警告] " + e);
                        // 统一收尾：保存 → 改 psz 路径 → 重载刷新（资源/零件同一套逻辑，无条件执行）
                        log("[修路径] 重建完成，统一保存+改psz+重载");
                        ComponentIO.FixPszAfterInsert(log);
                        return sb.ToString() + "\n" + rep;
                    }

                    case "weld":
                    {
                        bool proj = input["project"] != null && input["project"].Type == JTokenType.Boolean
                                    && (bool)input["project"];
                        bool bindOnly = input["bind_only"] != null && input["bind_only"].Type == JTokenType.Boolean
                                        && (bool)input["bind_only"];
                        var rep = WeldPointIO.RebuildWeldPoints(file, proj, bindOnly, log);
                        foreach (var e in rep.Errors) log("[警告] " + e);
                        return sb.ToString() + "\n" + rep;
                    }

                    default:
                        return "Error: 未知 kind \"" + kind + "\"（应为 resource|part|weld）。";
                }
            }
            catch (Exception ex)
            {
                return "Error: " + ex.GetType().Name + ": " + ex.Message;
            }
        }
        /// <summary>从 JSON 输入取 double 值，缺失或无效返回 0。</summary>
        private static double NumVal(JObject input, string key, double fallback = 0.0)
        {
            if (input == null) return fallback;
            var v = input[key];
            if (v == null || v.Type == JTokenType.Null) return fallback;
            double d;
            return double.TryParse(v.ToString(), out d) ? d : fallback;
        }
    }
}
