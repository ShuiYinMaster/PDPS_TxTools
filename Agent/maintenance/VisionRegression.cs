using System;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using TxTools.Agent.Core;

public static class VisionRegression
{
    static int count;
    static void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; Console.WriteLine("PASS " + name); }
    public static void Main()
    {
        const string vision = "deepseek-v4-flash-vision-exp";
        Check(ModelRouter.FindByModelId(vision, "deepseek").SupportsVision, "DeepSeek vision-exp supported");
        Check(!ModelRouter.FindByModelId("deepseek-v4-flash", "deepseek").SupportsVision, "ordinary Flash remains text-only");
        Check(!ModelRouter.FindByModelId("deepseek-v4-pro", "deepseek").SupportsVision, "ordinary Pro remains text-only");
        Check(ModelFilter.IsUsable(vision), "vision-exp survives model filtering");
        Check(ModelRouter.SelectVisionFor("deepseek-v4-flash", "deepseek", null, p => p == "deepseek").ModelId == vision, "DeepSeek key alone routes vision");
        Check(ModelRouter.SelectVisionFor("kimi-k2.6", "kimi", null, p => true).ModelId == "kimi-k2.6", "current vision model preferred");
        Check(ModelRouter.SelectVisionFor("deepseek-v4-pro", "deepseek", null, p => true).ModelId == vision, "same provider preferred");
        Check(ModelRouter.SelectVisionFor("deepseek-v4-pro", "deepseek", "kimi", p => true).Provider == "kimi", "explicit override respected");
        Check(ModelRouter.SelectVisionFor(vision, "deepseek", "kimi", p => p == "deepseek") == null, "explicit unavailable provider never silently falls back");
        Check(ModelRouter.SelectVisionFor(vision, "deepseek", null, p => false) == null, "no configured providers");
        Check(ModelRouter.SelectVisionFor("deepseek-v4-pro", "deepseek", null, p => p == "qwen").Provider == "qwen", "automatic candidate fallback when current provider unavailable");
        var before = ModelRouter.PreferredVisionProvider;
        ModelRouter.SelectVisionFor(vision, "deepseek", "qwen", p => true);
        Check(ModelRouter.PreferredVisionProvider == before, "explicit routing does not mutate shared preference");
        var message = JsonConvert.DeserializeObject<ChatMessage>("{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"inspect\"},{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/png;base64,AQID\",\"detail\":\"high\"}}]}");
        Check(message.HasImages && message.Content == "inspect", "multimodal deserialize retains image and text");
        Check(JsonConvert.SerializeObject(message).Contains("data:image/png;base64,AQID"), "image survives JSON round trip");
        message.ContentPayload = "text only";
        Check(!message.HasImages, "replacing content does not retain stale image");
        var helper = typeof(AnalyzeImageTool).Assembly.GetType("TxTools.Agent.Core.VisionSupport");
        string file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vision_test.bmp");
        using (var bitmap = new System.Drawing.Bitmap(2, 2)) bitmap.Save(file, System.Drawing.Imaging.ImageFormat.Bmp);
        var args = new object[] { null, file, null, null, null };
        var error = helper.GetMethod("LoadImage").Invoke(null, args);
        Check(error == null && (string)args[3] == "image/png", "BMP converted to supported PNG");
        var bytes = Convert.FromBase64String((string)args[2]);
        Check(bytes[0] == 137 && bytes[1] == 80, "converted image has actual PNG signature");
        var invalid = (string)helper.GetMethod("Ask").Invoke(null, new object[] { "test", "", "image/png", "invalid", null, null });
        Check(invalid.StartsWith("Error:"), "invalid detail fails before any network access");
        Console.WriteLine("TOTAL PASS: " + count);
    }
}
