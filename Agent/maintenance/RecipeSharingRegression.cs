using System;
using System.Collections.Generic;
using System.IO;
using TxTools.Agent.Core;

// Compile against the isolated review build; never executes recipe code.
public static class RecipeSharingRegression
{
    static int checks;
    static readonly List<string> created = new List<string>();
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAILED: " + name);
        checks++;
        Console.WriteLine("PASS " + name);
    }
    static Recipe Import(string text)
    {
        Recipe imported;
        string error;
        if (!RecipeStore.TryImportMarkdown(text, out imported, out error)) throw new Exception(error);
        created.Add(imported.Id);
        return imported;
    }
    static void Reject(string text, string name)
    {
        Recipe imported;
        string error;
        int before = RecipeStore.All().Count;
        Check(!RecipeStore.TryImportMarkdown(text, out imported, out error) && imported == null && !string.IsNullOrEmpty(error), name);
        Check(RecipeStore.All().Count == before, name + " leaves storage unchanged");
    }
    public static void Main()
    {
        try { Run(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
            Environment.ExitCode = 1;
        }
    }
    static void Run()
    {
        // The assembly is copied to artifacts/recipe-review; reject accidental use against deployed memory.
        var folder = Path.GetFullPath(MdStore.FolderPath("recipes"));
        Check(folder.IndexOf(Path.Combine("artifacts", "recipe-review", "memory", "recipes"), StringComparison.OrdinalIgnoreCase) >= 0,
            "test storage isolated from deployed recipes");
        var original = new Recipe {
            Id = "regression_" + Guid.NewGuid().ToString("N"), Name = "测试分享_" + Guid.NewGuid().ToString("N"),
            Description = "**能力**：按选中机器人导出数据。\n\n- 保留姿态\n- 保留颜色",
            Lang = "csharp", Code = "var text = @\"```\n## 参数\n## 代码\n```\";\n// 最后一行",
            SourceSnippet = "local_snippet", RunCount = 7, FailCount = 2, LastRunUtc = DateTime.UtcNow,
            Params = new List<RecipeParam> {
                new RecipeParam { Name = "robot", Label = "机器人", Kind = "object", TypeHint = "TxRobot", Help = "选择机器人" },
                new RecipeParam { Name = "filename", Label = "输出文件", Kind = "text", Required = false, Default = "result.txt" }
            }
        };
        try
        {
            created.Add(original.Id);
            Check(RecipeStore.Upsert(original).StartsWith("已保存配方:"), "save original recipe");
            var exported = RecipeStore.ExportMarkdown(original);
            Check(exported.Contains("recipe_format: 1") && exported.Contains("````csharp"), "versioned Markdown protects embedded fences");
            Check(!exported.Contains("local_snippet") && !exported.Contains("run_count: 7"), "sharing omits local provenance and run counts");
            var imported = Import(exported);
            Check(imported.Id != original.Id && imported.Name.Contains("导入 2"), "same-name import creates a named copy");
            Check(imported.Code == original.Code, "code round trip");
            Check(imported.Description.Replace("\r\n", "\n") == original.Description, "description round trip across Windows newlines");
            Check(imported.Params.Count == 2 && imported.Params[0].TypeHint == "TxRobot" && imported.Params[1].Default == "result.txt", "parameter declarations round trip");
            Check(imported.RunCount == 0 && imported.FailCount == 0 && imported.LastRunUtc == default(DateTime) && string.IsNullOrEmpty(imported.SourceSnippet), "import starts with local execution history empty");
            Check(RecipeStore.Get(imported.Id).Code == original.Code, "import persists readable executable definition");
            Check(RecipeStore.Get(original.Id).RunCount == 7, "existing recipe remains intact");
            Check(Import(exported).Name.Contains("导入 3"), "repeated imports stay distinct");
            // Reconstruct legacy metadata without the new format key.
            var old = "---\nkey: ../../outside\nname: 旧配方\nlang: python\n---\n\n## 参数\n\n```json\n[]\n```\n\n## 代码\n\n```python\nprint('ok')\n```\n";
            var fromLegacy = Import("\uFEFF" + old);
            Check(fromLegacy.Id.StartsWith("import_") && fromLegacy.Code == "print('ok')" && fromLegacy.Params.Count == 0, "legacy Markdown BOM and untrusted key handled");
            var display = RecipeStore.DisplayMarkdown(imported);
            Check(display.StartsWith("## 配方：") && !display.StartsWith("---") && display.Contains("## 参数") && display.Contains("## 代码"), "chat document has valid title and complete sections");
            Check(RecipeStore.CandidateDescription(new Snippet { Origin = "auto", Description = "var robot = ...", Code = "// 批量对齐选中设备的 Z 轴\nvar robot = 1;" }) == "批量对齐选中设备的 Z 轴", "automatic candidates use existing purpose comments");
            Check(RecipeStore.CandidateDescription(new Snippet { Origin = "manual", Description = "按颜色拆分并导出", Code = "// 内部实现的细节说明" }) == "按颜色拆分并导出", "manual purpose description preserved");
            Check(RecipeStore.CandidateDescription(new Snippet { Code = "var x = 1;" }).Contains("未记录用途说明"), "missing purpose is explicit");
            Reject(exported.Replace("recipe_format: 1", "recipe_format: 999"), "unsupported format rejected");
            Reject(old.Replace("lang: python", "lang: javascript"), "unsupported language rejected");
            Reject(old.Replace("[]", "null"), "null parameter list rejected");
            Reject(old.Replace("[]", "[broken]"), "malformed JSON rejected");
            Reject(old.Replace("[]", "[{\"Name\":\"bad name\",\"Kind\":\"object\"}]"), "invalid parameter identifier rejected");
            Reject(old.Replace("[]", "[{\"Name\":\"x\",\"Kind\":\"bad\"}]"), "unknown parameter kind rejected");
            Reject(old.Replace("[]", "[{\"Name\":\"x\"},{\"Name\":\"x\"}]"), "duplicate parameter names rejected");
            Reject(old.Replace("name: 旧配方", "name: "), "missing name rejected");
            Reject(old.Replace("## 参数", "## 其他"), "missing parameter section rejected");
            Reject(old.Substring(0, old.LastIndexOf("```", StringComparison.Ordinal)), "unclosed code fence rejected");
            Reject("# 普通文档\n不是配方", "non-recipe Markdown rejected");
            Console.WriteLine("Recipe sharing regression passed: " + checks + " checks.");
        }
        finally
        {
            foreach (var id in created) RecipeStore.Delete(id);
        }
    }
}
