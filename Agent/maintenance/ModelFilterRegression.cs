using System;
using System.Collections.Generic;
using System.Linq;
using TxTools.Agent.Core;

internal static class ModelFilterRegression
{
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }

    private static void Main()
    {
        var catalog = new List<string>
        {
            "qwen-plus", "qwen-plus-2026-07-15", "qwen-plus-20260716",
            "qwen2.5-7b-instruct", "deepseek-r1-distill-qwen-7b",
            "text-embedding-v4", "qwen-tts", "wan2-video", "", " ", null,
            "QWEN-PLUS"
        };
        for (int i = 0; i < 60; i++) catalog.Add("model-" + i);

        ModelFilter.Whitelist["qwen"] = new[] { "qwen-plus" };
        var result = ModelFilter.Clean("QWEN", catalog);
        Check(result.Count == 68, "Qwen retains every distinct, nonempty model, beyond the 40 item limit");
        Check(result.Contains("qwen-plus-2026-07-15") && result.Contains("qwen-plus-20260716"),
            "Qwen date snapshots remain separate options");
        Check(result.Contains("qwen2.5-7b-instruct") && result.Contains("deepseek-r1-distill-qwen-7b"),
            "Qwen small and distilled models are retained");
        Check(result.Contains("text-embedding-v4") && result.Contains("qwen-tts") && result.Contains("wan2-video"),
            "Qwen names are not filtered by modality");
        Check(result.Count(m => string.Equals(m, "qwen-plus", StringComparison.OrdinalIgnoreCase)) == 1,
            "Duplicate options are still removed");
        Check(ModelFilter.Clean("qwen", null).Count == 0, "Empty Qwen catalog is handled");

        var others = ModelFilter.Clean("openai", catalog);
        Check(others.Count <= ModelFilter.MaxPerProvider, "Other providers keep the existing item limit");
        Check(!others.Contains("text-embedding-v4") && !others.Contains("qwen-tts"),
            "Other providers retain the existing name rules");
        var snapshots = ModelFilter.Clean("deepseek", new[] { "deepseek-chat", "deepseek-chat-2026-07-15" });
        Check(snapshots.SequenceEqual(new[] { "deepseek-chat" }), "Other providers still collapse snapshots");
        ModelFilter.Whitelist["openai"] = new[] { "gpt-4o" };
        var allowed = ModelFilter.Clean("openai", new[] { "gpt-4o", "gpt-4-turbo" });
        Check(allowed.SequenceEqual(new[] { "gpt-4o" }), "Other provider whitelists still apply");
        Console.WriteLine("Model filter regression passed: full Qwen catalog and unchanged other-provider rules.");
    }
}
