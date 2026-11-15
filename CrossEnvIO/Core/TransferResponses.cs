using System;
using Newtonsoft.Json.Linq;

namespace TxTools.CrossEnvIO
{
    internal static class TransferResponses
    {
        // RPC 适配器可将工具的 ok:false 放入 Error；有匹配的任务结果时仍是已知响应。
        internal static JObject ReadFailure(string tool, string raw, JObject input)
        {
            JObject value;
            try { value = JObject.Parse(raw ?? ""); } catch { return null; }
            if ((bool?)value["ok"] != false) return null;
            if (tool == "crossenv_transfer_check" && value["errors"] is JArray) return value;
            if (tool == "crossenv_transfer_status" && value["error"]?.Type == JTokenType.String) return value;
            if (tool != "crossenv_transfer_execute") return null;
            var job = value["job"] as JObject;
            if (job == null) return value["not_started"]?.Type == JTokenType.Boolean ? value : null;
            var plan = input?["plan"]?.ToObject<TransferPlan>();
            string state = (string)job["State"];
            return plan != null && (string)job["Id"] == plan.JobId && (string)job["Fingerprint"] == plan.Stamp()
                && (state == "running" || state == "succeeded" || state == "failed") ? value : null;
        }
    }
}
