using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Tecnomatix.Engineering;

namespace TxTools.Agent.Core
{
    /// <summary>Shared execution and selection boundary for the sidebar and desktop launcher.</summary>
    internal static class RecipeUiActions
    {
        private static int _running;
        private static string _runningId;
        public static event Action Changed;
        public static bool IsRunning => Volatile.Read(ref _running) != 0;
        public static string RunningId => _runningId;

        public static string CurrentStudyKey()
        {
            try
            {
                dynamic study = TxApplication.ActiveDocument?.CurrentStudy;
                return study == null ? null : (string)study.Name;
            }
            catch { return null; }
        }

        public static JObject List()
        {
            var all = RecipeStore.All().ToList();
            var prefs = UserPrefsStore.Load();
            return new JObject
            {
                ["type"] = "recipe.list.result", ["ok"] = true,
                ["study"] = CurrentStudyKey(), ["busy"] = IsRunning, ["runningId"] = _runningId,
                ["favorites"] = new JArray(prefs.PinnedRecipeIds ?? new List<string>()),
                ["recent"] = new JArray(all.Where(r => r.LastRunUtc != default(DateTime))
                    .OrderByDescending(r => r.LastRunUtc).Take(5).Select(r => r.Id)),
                ["savedArgs"] = JObject.FromObject(prefs.RecipeArguments ?? new Dictionary<string, Dictionary<string, string>>()),
                ["recipes"] = new JArray(all.Select(r => new JObject
                {
                    ["id"] = r.Id, ["name"] = r.Name, ["description"] = r.Description,
                    ["lang"] = r.Lang, ["runCount"] = r.RunCount, ["failCount"] = r.FailCount,
                    ["params"] = new JArray((r.Params ?? new List<RecipeParam>()).Select(p => new JObject
                    {
                        ["name"] = p.Name, ["label"] = p.Label, ["kind"] = p.Kind,
                        ["typeHint"] = p.TypeHint, ["required"] = p.Required,
                        ["def"] = p.Default, ["help"] = p.Help
                    }))
                }))
            };
        }

        public static JObject PickSelection(bool multi)
        {
            try
            {
                var sel = TxApplication.ActiveSelection.GetItems();
                if (sel == null || sel.Count == 0) return Error("recipe.pick.result", "请先在软件中选中对象，再点击“取当前选择”。");
                if (!multi && sel.Count > 1) return Error("recipe.pick.result", "此参数只需要一个对象，当前选中了 " + sel.Count + " 个，请重新选择。");
                return new JObject
                {
                    ["type"] = "recipe.pick.result", ["ok"] = true,
                    ["id"] = multi ? string.Join("|", sel.Select(o => o.Id)) : sel[0].Id,
                    ["name"] = sel[0].Name, ["count"] = multi ? sel.Count : 1,
                    ["objectType"] = sel[0].GetType().Name, ["study"] = CurrentStudyKey()
                };
            }
            catch (Exception ex) { return Error("recipe.pick.result", "取选择失败: " + ex.Message); }
        }

        public static JObject Error(string type, string error, string id = null)
        {
            return new JObject { ["type"] = type, ["ok"] = false, ["error"] = error, ["recipeId"] = id };
        }

        public static void Run(int seq, JObject msg, Action<int, JObject> reply)
        {
            string id = (string)msg["recipeId"];
            var r = RecipeStore.Get(id);
            if (r == null) { reply(seq, Error("recipe.run.result", "配方不存在，可能已被删除。", id)); return; }
            bool checkStudy = msg["study"] != null;
            string expectedStudy = (string)msg["study"];
            Func<string> validateStudy = () => checkStudy && !string.Equals(expectedStudy, CurrentStudyKey(), StringComparison.Ordinal)
                ? "当前研究已切换，请重新选取对象后执行。" : null;
            string error = validateStudy();
            if (error != null) { reply(seq, Error("recipe.run.result", error, id)); return; }
            var args = new Dictionary<string, string>();
            var supplied = msg["args"] as JObject;
            if (supplied != null) foreach (var kv in supplied) args[kv.Key] = (string)kv.Value;
            string full = RecipeRunner.BuildCode(r, args, out error);
            if (full == null) { reply(seq, Error("recipe.run.result", error, id)); return; }
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            {
                reply(seq, Error("recipe.run.result", "已有配方正在执行，请等待结果后再运行。", id)); return;
            }
            _runningId = id;
            try { UserPrefsStore.UpdateRecipeArguments(r, args); } catch { }
            TxTools.Agent.TxAgentCommand.SetPetState("working", r.Name);
            NotifyChanged();
            // Solidified recipes retain their existing execution policy: confirm parameters,
            // execute the saved code with its named Undo block, then record the outcome.
            Task.Run(() =>
            {
                bool ok = false;
                string text = null;
                try
                {
                    if (SnippetStore.NormalizeLang(r.Lang) == "python")
                    {
                        PsContext.Current.Run(() =>
                        {
                            var changed = validateStudy();
                            if (changed != null) { text = changed; return; }
                            var result = Scripting.PythonHostProvider.Instance.Run(full, Scripting.PythonRunMode.Execute, "配方: " + r.Name);
                            ok = result != null && result.Success;
                            text = result?.ToAgentText() ?? "(无结果)";
                        });
                    }
                    else text = Ps.PsBridge.RunCSharp(full, out ok, "配方: " + r.Name, validateStudy);
                    try { RecipeStore.RecordRun(id, ok); }
                    catch (Exception ex) { try { AuditLog.Write("[warn] [Recipe] 记录结果失败: " + ex.Message); } catch { } }
                    try { AuditLog.Write((ok ? "[info]" : "[warn]") + " [Recipe] " + r.Name + " 执行" + (ok ? "成功" : "失败")); } catch { }
                }
                catch (Exception ex) { ok = false; text = "执行异常: " + ex.Message; }
                finally
                {
                    _runningId = null;
                    Interlocked.Exchange(ref _running, 0);
                    TxTools.Agent.TxAgentCommand.SetPetState(ok ? "success" : "error");
                    try { reply(seq, new JObject { ["type"] = "recipe.run.result", ["ok"] = ok, ["recipeId"] = id, ["text"] = text ?? "" }); }
                    finally { NotifyChanged(); }
                }
            });
        }

        private static void NotifyChanged()
        {
            var handlers = Changed;
            if (handlers == null) return;
            foreach (Action handler in handlers.GetInvocationList()) { try { handler(); } catch { } }
        }
    }
}
