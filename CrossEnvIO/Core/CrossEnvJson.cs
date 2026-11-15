using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TxTools.CrossEnvIO
{
    internal static class CrossEnvJson
    {
        // PS 可预加载缺少 JToken.ToString(Formatting) 的旧版 Json.NET。
        internal static string ToHostJson(this JToken value, Formatting formatting)
            => JsonConvert.SerializeObject(value, formatting);
    }
}
