// TxTools.Agent / Core / RecipeStore.cs
// 配方持久化。明文 JSON 存到插件文件夹下的 recipes.json (配方非机密，不需加密)。
// 路径策略与 KeyStore 一致：插件目录优先，不可写则回退 %LOCALAPPDATA%\TxTools.Agent。

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;

namespace TxTools.Agent.Core
{
    public static class RecipeStore
    {
        private const string FileName = "recipes.json";

        public static List<Recipe> Load()
        {
            foreach (var path in CandidatePaths())
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var json = File.ReadAllText(path, Encoding.UTF8);
                    var list = JsonConvert.DeserializeObject<List<Recipe>>(json);
                    if (list != null) return list;
                }
                catch { /* 换下一个候选 */ }
            }
            return new List<Recipe>();
        }

        public static string Save(List<Recipe> recipes)
        {
            var json = JsonConvert.SerializeObject(recipes ?? new List<Recipe>(), Formatting.Indented);
            Exception last = null;
            foreach (var path in CandidatePaths())
            {
                try
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(path, json, Encoding.UTF8);
                    return path;
                }
                catch (Exception ex) { last = ex; }
            }
            throw new IOException("无法写入配方文件。", last);
        }

        /// <summary>新增或按 Name 覆盖一条配方，并持久化。</summary>
        public static string Upsert(Recipe recipe)
        {
            if (recipe == null || string.IsNullOrWhiteSpace(recipe.Name))
                throw new ArgumentException("配方必须有 Name。");
            var all = Load();
            all.RemoveAll(r => string.Equals(r.Name, recipe.Name, StringComparison.Ordinal));
            all.Add(recipe);
            return Save(all);
        }

        /// <summary>按 Name 删除一条配方，删到返回 true。</summary>
        public static bool Remove(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            var all = Load();
            int n = all.RemoveAll(r => string.Equals(r.Name, name, StringComparison.Ordinal));
            if (n == 0) return false;
            Save(all);
            return true;
        }

        private static string[] CandidatePaths()
        {
            string pluginDir = null;
            try { pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); }
            catch { }

            var localDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TxTools.Agent");

            if (string.IsNullOrEmpty(pluginDir))
                return new[] { Path.Combine(localDir, FileName) };

            return new[]
            {
                Path.Combine(pluginDir, FileName),
                Path.Combine(localDir, FileName)
            };
        }
    }
}
