// TxTools.Agent / Tools / Vision / VisionTools.cs
//
// 图像识别工具。
//
// 为什么单独走一个模型:
//   DeepSeek 系列不支持视觉,主对话模型直接看不了图。
//   与其让用户手动切模型(切了之后工具调用等能力又可能受影响),
//   不如让主模型把图【委托】给视觉模型,拿回一段文字描述继续干活 ——
//   主对话上下文里留下的是描述文本,不是几十万 token 的图片。
//
// 两个工具:
//   analyze_image     —— 看已上传的文件
//   analyze_viewport  —— 截当前 3D 视口再看,不用用户手动截图上传

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace TxTools.Agent.Core
{
    public sealed class AnalyzeImageTool : TxAgentToolBase, ITxOffUiThreadTool
    {
        public override string Name { get { return "analyze_image"; } }

        public override string Description
        {
            get
            {
                return "看图。把已上传的图片交给视觉模型识别，返回文字描述。"
                     + "【当前主对话模型不支持视觉，看图必须用本工具】。"
                     + "适用：用户上传的截图/照片/图纸/报错框，需要知道图里有什么、写了什么字、"
                     + "布局是怎样的。question 写清你要从图里得到什么，问得越具体回答越有用——"
                     + "「这张图里有几台机器人，分别叫什么」远好于「描述这张图」。"
                     + "注意本工具只回文字，图片本身不会进入对话上下文。";
            }
        }

        public override bool IsReadOnly { get { return true; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    'type': 'object',
                    'required': ['question'],
                    'properties': {
                        'file_id':  { 'type': 'string', 'description': '已上传图片的 file_id。留空则用最近一次上传的图片' },
                        'path':     { 'type': 'string', 'description': '本地图片绝对路径。与 file_id 二选一' },
                        'question': { 'type': 'string', 'description': '你想从图里得到什么信息，尽量具体' },
                        'detail':   { 'type': 'string', 'description': 'low(默认，省钱，判断有无/是什么/大致布局够用) | high(要读小字、看细节时才用)' },
                        'provider': { 'type': 'string', 'description': '可选，指定 kimi 或 qwen。留空自动选' }
                    }
                }");
            }
        }

        public override string Execute(JObject input)
        {
            var question = GetString(input, "question");
            var fileId = GetString(input, "file_id");
            var path = GetString(input, "path");
            // 默认 low:图像 token 随分辨率涨，多数判断题(有没有/是什么/在哪)low 就够，
            // 只有读小字、看细节才值得上 high。
            var detail = GetString(input, "detail", "low");
            var provider = GetString(input, "provider");

            if (string.IsNullOrWhiteSpace(question))
                return "Error: question 必需 —— 说清你要从图里得到什么。";

            string base64, mime, source;
            var err = VisionSupport.LoadImage(fileId, path, out base64, out mime, out source);
            if (err != null) return "Error: " + err;

            return VisionSupport.Ask(question, base64, mime, detail, provider, source);
        }
    }

    // ──────────────────────────────────────────────────────────────

    public sealed class AnalyzeViewportTool : TxAgentToolBase, ITxOffUiThreadTool
    {
        public override string Name { get { return "analyze_viewport"; } }

        public override string Description
        {
            get
            {
                return "截取当前 3D 视口并交给视觉模型识别，一步完成「截图 + 看图」。"
                     + "适用：需要确认布局是否合理、干涉是否明显、某个对象在画面里的相对位置、"
                     + "或者操作之后想直观确认效果。"
                     + "配合 set_camera_view 先摆好角度再调用，效果更好。"
                     + "question 要具体，比如「机器人和夹具之间有没有明显干涉」。";
            }
        }

        public override bool IsReadOnly { get { return true; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    'type': 'object',
                    'required': ['question'],
                    'properties': {
                        'question': { 'type': 'string', 'description': '你想从画面里确认什么，尽量具体' },
                        'width':    { 'type': 'integer', 'description': '截图宽度，默认 1024。看整体布局够用，要看细节再调大' },
                        'height':   { 'type': 'integer', 'description': '截图高度，默认 576' },
                        'detail':   { 'type': 'string', 'description': 'low(默认) | high(要看细节时才用)' },
                        'provider': { 'type': 'string', 'description': '可选，kimi 或 qwen' }
                    }
                }");
            }
        }

        public override string Execute(JObject input)
        {
            var question = GetString(input, "question");
            if (string.IsNullOrWhiteSpace(question))
                return "Error: question 必需。";

            // 分辨率直接决定图像 token 数,进而决定这次看图多少钱。
            // 1024x576 判断布局/干涉/相对位置足够;要读铭牌小字再让模型显式调大。
            int width = input["width"] != null && input["width"].Type == JTokenType.Integer
                ? (int)input["width"] : 1024;
            int height = input["height"] != null && input["height"].Type == JTokenType.Integer
                ? (int)input["height"] : 576;

            var detail = GetString(input, "detail", "low");
            var provider = GetString(input, "provider");

            // 截图要在 PS 主线程做;本工具标了 ITxOffUiThreadTool 跑在后台线程,
            // 所以这里显式回主线程截一次,拿到路径后再走网络调用。
            string shotPath;
            try
            {
                shotPath = PsContext.Current.Run<string>(delegate
                {
                    return VisionSupport.CaptureViewport(width, height);
                });
            }
            catch (Exception ex)
            {
                return "Error: 截取视口失败 - " + ex.Message;
            }

            if (string.IsNullOrEmpty(shotPath) || !File.Exists(shotPath))
                return "Error: 截取视口失败，没有生成图片文件。";

            string base64, mime, source;
            var err = VisionSupport.LoadImage(null, shotPath, out base64, out mime, out source);
            if (err != null) return "Error: " + err;

            var answer = VisionSupport.Ask(question, base64, mime, detail, provider, "当前 3D 视口");

            try { File.Delete(shotPath); } catch { }
            return answer;
        }
    }

    // ──────────────────────────────────────────────────────────────

    internal static class VisionSupport
    {
        /// <summary>单张图 base64 后的上限。太大既慢又贵,而且多数 provider 有硬限制。</summary>
        private const int MaxBase64Chars = 8 * 1024 * 1024;

        /// <summary>把图片读成 base64。返回错误说明,null 表示成功。</summary>
        public static string LoadImage(string fileId, string path,
            out string base64, out string mime, out string source)
        {
            base64 = null; mime = null; source = null;

            try
            {
                if (string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(fileId))
                {
                    var uf = UploadStore.Get(fileId);
                    if (uf == null)
                        return "找不到 file_id=" + fileId + " 对应的文件。"
                             + "注意上传文件存在临时目录，关闭窗口后会被清理 —— "
                             + "若这是从历史对话里翻出来的 file_id，需要用户重新上传。";

                    // 先按扩展名挡掉非图片，省一次读盘 + 一次无谓的网络调用
                    if (MimeOf(uf.OriginalName ?? uf.LocalPath) == null)
                        return "file_id=" + fileId + " 是 " + uf.OriginalName
                             + "，不是图片(需 png/jpg/jpeg/gif/webp/bmp)。"
                             + "文本/表格类文件请改用 read_uploaded_file。";

                    path = uf.LocalPath;
                    source = uf.OriginalName;
                }

                if (string.IsNullOrWhiteSpace(path))
                    return "未提供 file_id 或 path。";

                if (!File.Exists(path))
                    return "文件不存在: " + path;

                // 用真实路径判断 —— UploadStore 存盘时文件名带 id 前缀，扩展名仍保留
                mime = MimeOf(path);
                if (mime == null)
                    return "不是支持的图片格式(需 png/jpg/jpeg/gif/webp/bmp): " + Path.GetExtension(path);

                var bytes = File.ReadAllBytes(path);
                base64 = Convert.ToBase64String(bytes);

                if (base64.Length > MaxBase64Chars)
                    return "图片过大(" + (bytes.Length / 1024 / 1024) + " MB)，请先压缩或截取局部后重试。";

                if (source == null) source = Path.GetFileName(path);
                return null;
            }
            catch (Exception ex)
            {
                return "读取图片失败: " + ex.Message;
            }
        }

        /// <summary>调视觉模型。同步阻塞 —— 调用方已在后台线程。</summary>
        public static string Ask(string question, string base64, string mime,
            string detail, string provider, string source)
        {
            var spec = PickModel(provider);
            if (spec == null)
            {
                var have = ModelRouter.AvailableProviders();
                return "没有可用的视觉模型。当前主模型不支持看图，需要在设置里配置 "
                     + "Kimi 或 千问(阿里百炼) 的 API key。"
                     + (have.Count > 0 ? "已配置的 provider: " + string.Join(", ", have) : "当前未配置任何其它 provider。");
            }

            var client = ModelRouter.GetClient(spec);
            if (client == null)
                return "取不到 " + spec.Provider + " 的客户端，请检查 API key 配置。";

            var img = ContentPart.FromImageBase64(base64, mime);
            if (!string.IsNullOrEmpty(detail)) img.ImageUrl.Detail = detail;

            var userMsg = new ChatMessage
            {
                Role = "user",
                Content = question,
                ContentParts = new List<ContentPart>
                {
                    ContentPart.FromText(question),
                    img
                }
            };

            var req = new ChatRequest
            {
                Model = spec.ModelId,
                MaxTokens = 2048,
                Temperature = 0.2,
                Stream = false,
                Messages = new List<ChatMessage>
                {
                    new ChatMessage("system",
                        "你是工业仿真场景的图像分析助手。只描述图中【确实看得到】的内容，"
                        + "看不清或不确定就明说，绝不猜测或补全。涉及数量、名称、文字时逐一列出。"),
                    userMsg
                }
            };

            try
            {
                var resp = client.SendAsync(req, CancellationToken.None)
                                 .GetAwaiter().GetResult();

                if (resp == null || resp.Choices == null || resp.Choices.Count == 0)
                    return "视觉模型返回空响应。";

                var text = resp.Choices[0].Message != null ? resp.Choices[0].Message.Content : null;
                if (string.IsNullOrWhiteSpace(text))
                    return "视觉模型没有返回内容。";

                var sb = new StringBuilder();
                sb.Append("【").Append(spec.ToString()).Append(" 看图结果");
                if (!string.IsNullOrEmpty(source)) sb.Append(" · ").Append(source);
                sb.AppendLine("】");
                sb.AppendLine(text.Trim());

                if (resp.Usage != null)
                    sb.Append("(本次看图消耗 ").Append(resp.Usage.TotalTokens).Append(" tokens)");

                return sb.ToString();
            }
            catch (LlmApiException ex)
            {
                return "视觉模型 API 错误 [" + spec + "]: " + ex.Message;
            }
            catch (Exception ex)
            {
                return "调用视觉模型失败 [" + spec + "]: " + ex.GetType().Name + ": " + ex.Message;
            }
        }

        private static ModelSpec PickModel(string provider)
        {
            if (!string.IsNullOrWhiteSpace(provider))
            {
                var saved = ModelRouter.PreferredVisionProvider;
                try
                {
                    ModelRouter.PreferredVisionProvider = provider;
                    return ModelRouter.Select(TaskScene.Vision, null);
                }
                finally { ModelRouter.PreferredVisionProvider = saved; }
            }
            return ModelRouter.Select(TaskScene.Vision, null);
        }

        private static string MimeOf(string path)
        {
            switch ((Path.GetExtension(path) ?? "").ToLowerInvariant())
            {
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                case ".bmp": return "image/bmp";
                default: return null;
            }
        }

        /// <summary>截当前 3D 视口到临时文件。必须在 PS 主线程调用。</summary>
        public static string CaptureViewport(int width, int height)
        {
            var viewer = Tecnomatix.Engineering.TxApplication.ViewersManager.GraphicViewer;
            if (viewer == null) return null;

            var img = viewer.GetImage(new System.Drawing.Size(width, height), false);
            if (img == null) return null;

            var path = Path.Combine(Path.GetTempPath(),
                "txagent_vp_" + DateTime.Now.ToString("yyyyMMddHHmmssfff") + ".png");

            using (img)
                img.Save(path, System.Drawing.Imaging.ImageFormat.Png);

            return path;
        }
    }
}