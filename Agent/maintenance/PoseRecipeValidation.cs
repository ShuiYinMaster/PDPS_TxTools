using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using TxTools.Agent.Core;

// Validates import and compiles generated scripts against the installed SDK, without invoking them.
public static class PoseRecipeValidation
{
    public static void Main(string[] args)
    {
        try
        {
            string sdk = Path.GetFullPath(args[1]);
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs e)
            {
                var path = Path.Combine(sdk, new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
            Verify(Path.GetFullPath(args[0]), sdk);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
            Environment.ExitCode = 1;
        }
    }

    static void Verify(string path, string sdk)
    {
        if (Path.GetFullPath(MdStore.FolderPath("recipes")).IndexOf(
            Path.Combine("artifacts", "recipe-review", "memory", "recipes"), StringComparison.OrdinalIgnoreCase) < 0)
            throw new Exception("Validation must use the isolated review build.");
        Recipe recipe;
        string error;
        if (!RecipeStore.TryImportMarkdown(File.ReadAllText(path), out recipe, out error)) throw new Exception(error);
        try
        {
            if (recipe.Params.Count != 4 || recipe.Params[0].Required || recipe.Params[0].Default != "HOME")
                throw new Exception("Pose name must be optional and default to HOME.");
            Console.WriteLine("PASS Markdown import and optional pose parameter");
            var cases = new Dictionary<string, string>[] {
                new Dictionary<string, string>(),
                new Dictionary<string, string> { { "pose_name", "" } },
                new Dictionary<string, string> { { "pose_name", "OPEN" }, { "include_robots", "false" }, { "include_resources", "true" } },
                new Dictionary<string, string> { { "pose_name", "CLOSE" }, { "targets", "3,57,2,1|3,57,2,2" }, { "include_resources", "false" } }
            };
            var temp = Path.GetFullPath(Path.Combine("artifacts", "recipe-compile"));
            Directory.CreateDirectory(temp);
            var wrap = typeof(TxTools.Agent.Ps.CSharpRunner).GetMethod("Wrap", BindingFlags.NonPublic | BindingFlags.Static);
            for (int i = 0; i < cases.Length; i++)
            {
                string body = RecipeRunner.BuildCode(recipe, cases[i], out error);
                if (body == null) throw new Exception(error);
                if (i < 2 && !body.Contains("var pose_name = \"HOME\";")) throw new Exception("HOME fallback missing.");
                using (var provider = CodeDomProvider.CreateProvider("CSharp"))
                {
                    var cp = new CompilerParameters {
                        GenerateExecutable = false, GenerateInMemory = false,
                        OutputAssembly = Path.Combine(temp, "PoseRecipeCase" + i + ".dll"),
                        TempFiles = new TempFileCollection(temp, false)
                    };
                    cp.ReferencedAssemblies.Add("System.dll");
                    cp.ReferencedAssemblies.Add("System.Core.dll");
                    cp.ReferencedAssemblies.Add(Path.Combine(sdk, "Tecnomatix.Engineering.dll"));
                    var result = provider.CompileAssemblyFromSource(cp, (string)wrap.Invoke(null, new object[] { body }));
                    if (result.Errors.HasErrors)
                    {
                        foreach (CompilerError failure in result.Errors) if (!failure.IsWarning) Console.Error.WriteLine(failure);
                        throw new Exception("Script compile failed for case " + i);
                    }
                }
                Console.WriteLine("PASS generated C# 5 compilation case " + i);
            }
            Console.WriteLine("Pose recipe validation passed; no scene code was executed.");
        }
        finally { RecipeStore.Delete(recipe.Id); }
    }
}
