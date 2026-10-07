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
                    ["actions"] = JArray.FromObject(r.Actions ?? new List<RecipeAction>()),
                    ["params"] = new JArray((r.Params ?? new List<RecipeParam>()).Select(p => new JObject
                    {
                        ["name"] = p.Name, ["label"] = p.Label, ["kind"] = p.Kind,
                        ["typeHint"] = p.TypeHint, ["required"] = p.Required,
                        ["def"] = p.Default, ["help"] = p.Help, ["objectFilter"] = p.ObjectFilter,
                        ["objectTypes"] = new JArray(), ["objectTypesLoaded"] = !p.ObjectFilter,
                        ["choices"] = JArray.FromObject(p.Choices ?? new List<RecipeChoice>())
                    }))
                }))
            };
        }

        // Categories are concrete runtime types present in the current scene, never a fixed whitelist.
        // Scene access stays on the PS UI thread and occurs only when a visible object control requests it.
        private static List<ITxObject> SceneObjects()
        {
            if (TxApplication.ActiveDocument == null) throw new InvalidOperationException("请先打开研究。");
            dynamic document = TxApplication.ActiveDocument;
            var roots = new List<ITxObjectCollection>();
            Action<object> addRoot = value => { var root = value as ITxObjectCollection; if (root != null && !roots.Contains(root)) roots.Add(root); };
            try { addRoot(document.PhysicalRoot); } catch { }
            try { addRoot(document.ComponentRoot); } catch { }
            try { addRoot(document.ResourceRoot); } catch { }
            if (roots.Count == 0) throw new InvalidOperationException("当前研究无法读取对象，请使用当前选择。");
            return roots.SelectMany(root => root.GetAllDescendants(new TxTypeFilter(typeof(ITxObject))).Cast<ITxObject>())
                .Where(item => item != null).GroupBy(item => Convert.ToString(item.Id), StringComparer.Ordinal).Select(g => g.First()).ToList();
        }

        private static Type ExpectedType(RecipeParam parameter)
        {
            if (string.IsNullOrWhiteSpace(parameter.TypeHint)) return null;
            string hint = parameter.TypeHint.Trim();
            var type = typeof(ITxObject).Assembly.GetType(hint.Contains(".") ? hint : "Tecnomatix.Engineering." + hint);
            if (type == null) throw new InvalidOperationException("无法识别配方要求的对象类型。");
            return type;
        }

        // Labels follow the PS 2402 SDK type definitions. This table never adds absent scene types.
        private static readonly Dictionary<string, string> ObjectTypeLabels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Tx1DimensionalGeometry"] = "一维几何",
            ["Tx2Or3DimensionalGeometry"] = "二维或三维几何",
            ["TxComponent"] = "组件",
            ["TxCompoundPart"] = "复合零件",
            ["TxCompoundResource"] = "复合资源",
            ["TxDevice"] = "设备",
            ["TxGenericRoboticLocationOperation"] = "通用机器人位置操作",
            ["TxGeometry"] = "几何对象",
            ["TxGripper"] = "夹持器",
            ["TxGun"] = "焊枪",
            ["TxKinematicLink"] = "运动链节",
            ["TxNote"] = "注释",
            ["TxPartAppearance"] = "零件外观",
            ["TxRobot"] = "机器人",
            ["TxRoboticSeamLocationOperation"] = "机器人焊缝位置操作",
            ["TxRoboticViaLocationOperation"] = "机器人过渡位置操作",
            ["TxSeamMfgFeature"] = "焊缝制造特征",
            ["TxServoGun"] = "伺服焊枪",
            ["TxSweptVolume"] = "扫掠体",
            ["TxWeldLocationOperation"] = "焊点位置操作",
            ["TxWeldPoint"] = "焊点特征",
            ["TxGroup"] = "通用分组",
            ["TxTool"] = "工艺工具",
            ["TxFrame"] = "坐标系",
            ["TxSolid"] = "实体几何",
            ["TxSurface"] = "曲面几何"
        };

        private static string TypeLabel(Type type)
        {
            string label;
            if (ObjectTypeLabels.TryGetValue(type.Name, out label)) return label;
            return type.Name.StartsWith("Tx", StringComparison.Ordinal) ? type.Name.Substring(2) : type.Name;
        }

        public static JObject ObjectTypes(JObject request)
        {
            if (IsRunning) return Error("recipe.objectTypes.result", "配方正在执行，请完成后再刷新类型。");
            var parameter = RecipeStore.Get((string)request["recipeId"])?.Params?.FirstOrDefault(p => p.Name == (string)request["param"]);
            if (parameter == null || !parameter.ObjectFilter || (parameter.Kind != "object" && parameter.Kind != "objects"))
                return Error("recipe.objectTypes.result", "对象参数已变化，请刷新配方。");
            if (request["study"] != null && !string.Equals((string)request["study"], CurrentStudyKey(), StringComparison.Ordinal))
                return Error("recipe.objectTypes.result", "研究已切换，请刷新后重新选择。");
            try
            {
                var expected = ExpectedType(parameter);
                var types = SceneObjects().Where(item => expected == null || expected.IsInstanceOfType(item))
                    .Select(item => item.GetType()).GroupBy(type => type.FullName, StringComparer.Ordinal).Select(group => group.First()).OrderBy(TypeLabel, StringComparer.CurrentCulture).ThenBy(t => t.FullName, StringComparer.Ordinal);
                var choices = new JArray(new JObject { ["label"] = "全部类型", ["value"] = "all" });
                var rows = types.Select(type => new { Type = type, Label = TypeLabel(type) }).ToList();
                var duplicates = new HashSet<string>(rows.GroupBy(row => row.Label, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key), StringComparer.Ordinal);
                var labels = new HashSet<string>(StringComparer.Ordinal) { "全部类型" };
                foreach (var row in rows)
                {
                    string label = duplicates.Contains(row.Label) ? row.Label + "（" + row.Type.Name + "）" : row.Label;
                    if (!labels.Add(label)) { label = row.Label + "（" + row.Type.FullName + "）"; labels.Add(label); }
                    choices.Add(new JObject { ["label"] = label, ["value"] = row.Type.FullName });
                }
                return new JObject { ["type"] = "recipe.objectTypes.result", ["ok"] = true, ["study"] = CurrentStudyKey(), ["objectTypes"] = choices };
            }
            catch (Exception ex) { return Error("recipe.objectTypes.result", "读取场景类型失败：" + ex.Message); }
        }

        public static JObject PickSelection(JObject request)
        {
            if (IsRunning) return Error("recipe.pick.result", "配方正在执行，请完成后再选取对象。");
            var recipe = RecipeStore.Get((string)request["recipeId"]);
            var parameter = recipe?.Params?.FirstOrDefault(p => p.Name == (string)request["param"]);
            if (parameter == null || (parameter.Kind != "object" && parameter.Kind != "objects"))
                return Error("recipe.pick.result", "对象参数已变化，请刷新配方。");
            bool search = (bool?)request["search"] == true;
            string category = (string)request["objectType"] ?? "all";
            string name = ((string)request["objectName"] ?? "").Trim();
            if (request["study"] != null && !string.Equals((string)request["study"], CurrentStudyKey(), StringComparison.Ordinal))
                return Error("recipe.pick.result", "研究已切换，请刷新后重新选择。");
            if (!parameter.ObjectFilter && (search || category != "all" || name.Length > 0))
                return Error("recipe.pick.result", "此参数未启用对象筛选。");
            if (name.Length > 200) return Error("recipe.pick.result", "名称关键词请控制在 200 个字符内。");
            if (search && category == "all" && name.Length == 0) return Error("recipe.pick.result", "请选择对象类型或输入名称后查找。");
            try
            {
                Type expected = ExpectedType(parameter);
                IEnumerable<ITxObject> candidates = search ? SceneObjects() : TxApplication.ActiveSelection.GetItems().Cast<ITxObject>();
                // Match the exact scene class shown by the dropdown; base classes do not absorb other categories.
                if (category != "all" && !candidates.Any(item => item != null && item.GetType().FullName == category && (expected == null || expected.IsInstanceOfType(item))))
                    return Error("recipe.pick.result", search ? "场景中已无此类型，请刷新类型。" : "当前选择中没有该类型，请重新选取。");
                var selected = candidates.Where(item => item != null && (expected == null || expected.IsInstanceOfType(item))
                    && (category == "all" || item.GetType().FullName == category)
                    && (name.Length == 0 || (item.Name ?? "").IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0))
                    .GroupBy(item => Convert.ToString(item.Id), StringComparer.Ordinal).Select(g => g.First()).ToList();
                if (selected.Count == 0) return Error("recipe.pick.result", search ? "没有找到符合条件的对象。" : "当前选择中没有符合条件的对象。");
                if (parameter.Kind == "object" && selected.Count != 1) return Error("recipe.pick.result", "此参数只需要一个对象，请缩小条件或重新选择。");
                return PickResult(selected);
            }
            catch (Exception ex) { return Error("recipe.pick.result", "对象选择失败：" + ex.Message); }
        }

        private static JObject PickResult(IList<ITxObject> selected)
        {
            return new JObject { ["type"] = "recipe.pick.result", ["ok"] = true,
                ["id"] = string.Join("|", selected.Select(o => o.Id)), ["name"] = selected[0].Name,
                ["count"] = selected.Count, ["objectType"] = selected[0].GetType().Name, ["study"] = CurrentStudyKey() };
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
            string full = RecipeRunner.BuildCode(r, args, (string)msg["actionId"], out error);
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
            // execute the saved code in a scene Undo group, then record the outcome.
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
