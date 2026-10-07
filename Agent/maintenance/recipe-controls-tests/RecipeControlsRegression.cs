using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Newtonsoft.Json.Linq;
using TxTools.Agent.Core;
using TxTools.Agent.Tools;

public static class RecipeControlsRegression
{
    static int checks;
    static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
        checks++;
    }
    static string Resource(string name)
    {
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
    }
    public static int Main()
    {
        try
        {
            var folder = MdStore.FolderPath("recipes");
            Check(folder.Contains(Path.Combine("artifacts", "recipe-review")), "Storage must be isolated");
            // Clean only known fixtures in the suite's isolated folder.
            foreach (var file in Directory.GetFiles(folder, "*.md")) File.Delete(file);
            var marker = Path.Combine(folder, ".default-recipes-installed");
            if (File.Exists(marker)) File.Delete(marker);
            var visibility = MarkdownDoc.Parse(Resource("TxTools.Agent.LegacyRecipes.default_visibility.v1.md"));
            visibility.Set("run_count", 9); visibility.Set("fail_count", 2);
            Check(MdStore.Write("recipes", "default_visibility", visibility), "Legacy fixture write");
            var coordinates = MarkdownDoc.Parse(Resource("TxTools.Agent.LegacyRecipes.default_object_coordinates.v1.md"));
            coordinates.Set("name", "我的坐标配方");
            Check(MdStore.Write("recipes", "default_object_coordinates", coordinates), "Custom fixture write");
            var geometry = MarkdownDoc.Parse(Resource("TxTools.Agent.LegacyRecipes.default_geometry_weld_points.v3.md"));
            geometry.Set("run_count", 7);
            Check(MdStore.Write("recipes", "default_geometry_weld_points", geometry), "Geometry fixture write");
            File.WriteAllLines(marker, new[] { "default_visibility", "default_object_coordinates", "default_set_color" });
            var all = RecipeStore.All();
            var upgraded = RecipeStore.Get("default_visibility");
            Check(upgraded.Actions.Count == 2 && upgraded.RunCount == 9 && upgraded.FailCount == 2, "Default upgrade preserves history");
            Check(Directory.GetFiles(folder, "default_visibility.md.before-controls-*").Length > 0, "Upgrade backs up original");
            Check(RecipeStore.Get("default_object_coordinates").Name == "我的坐标配方" && RecipeStore.Get("default_object_coordinates").Actions.Count == 0, "Custom defaults stay unchanged");
            Check(RecipeStore.Get("default_set_color") == null, "Deleted defaults stay deleted");
            Check(all.All(r => RecipeStore.ValidateDefinition(r) == null), "All bundled definitions valid");
            Check(RecipeStore.Get("default_geometry_weld_points").RunCount == 7
                && RecipeStore.Get("default_geometry_weld_points").Description.Length < 100, "Geometry guide upgrade preserves execution history");

            Check(upgraded.Params.First(p => p.Name == "targets").ObjectFilter, "Default objects enable query controls");
            var fixtureArm = new Tecnomatix.Engineering.TxComponent { Id = "3,11", Name = "Fixture_Arm" };
            var fixtureBase = new Tecnomatix.Engineering.TxComponent { Id = "3,12", Name = "Fixture_Base" };
            var robot = new Tecnomatix.Engineering.TxRobot { Id = "3,13", Name = "Fixture_Robot" };
            var operation = new Tecnomatix.Engineering.TestObject { Id = "3,14", Name = "Fixture_Operation" };
            Tecnomatix.Engineering.TxApplication.ActiveDocument.PhysicalRoot.Items.AddRange(new Tecnomatix.Engineering.ITxObject[] { fixtureArm, fixtureBase, robot, operation, fixtureArm });
            Tecnomatix.Engineering.TxApplication.ActiveDocument.ComponentRoot.Items.Add(new Tecnomatix.Engineering.TxFrame { Id = "3,15", Name = "Fixture_Frame" });
            Tecnomatix.Engineering.TxApplication.ActiveDocument.ResourceRoot.Items.Add(fixtureArm);
            var query = new JObject { ["recipeId"] = "default_visibility", ["param"] = "targets", ["study"] = "test-study", ["search"] = true, ["objectType"] = "Tecnomatix.Engineering.TxComponent", ["objectName"] = "fixture" };
            var picked = RecipeUiActions.PickSelection(query);
            Check((bool)picked["ok"] && (string)picked["id"] == "3,11|3,12" && (int)picked["count"] == 2, "Scene type/name query deduplicates exact IDs");
            query["objectType"] = "all";
            Check((int)RecipeUiActions.PickSelection(query)["count"] == 4, "All scene roots queried while expected displayable type excludes operations");
            query["objectName"] = "missing";
            Check(!(bool)RecipeUiActions.PickSelection(query)["ok"], "Empty query result does not create binding");
            query["objectName"] = "";
            Check(!(bool)RecipeUiActions.PickSelection(query)["ok"], "Unbounded scene query rejected");
            query["objectType"] = "not-a-type";
            Check(!(bool)RecipeUiActions.PickSelection(query)["ok"], "Unknown object category rejected");
            query["objectType"] = "Tecnomatix.Engineering.TxComponent"; query["study"] = "other-study";
            Check(!(bool)RecipeUiActions.PickSelection(query)["ok"], "Query cannot cross study boundary");
            query["study"] = "test-study"; query["search"] = false;
            Tecnomatix.Engineering.TxApplication.ActiveSelection.Items.AddRange(new Tecnomatix.Engineering.ITxObject[] { fixtureArm, robot, operation });
            Check((string)RecipeUiActions.PickSelection(query)["id"] == "3,11", "Current selection uses same filters");
            query["recipeId"] = "default_object_coordinates";
            Check(!(bool)RecipeUiActions.PickSelection(query)["ok"], "Disabled query cannot be bypassed by message");

            var qualified = new Recipe { Id = "qualified_type", Name = "完整类型名称", Code = "return targets;", Params = new List<RecipeParam> { new RecipeParam { Name = "targets", Kind = "objects", TypeHint = "Tecnomatix.Engineering.ITxDisplayableObject", ObjectFilter = true } } };
            Check(RecipeStore.Upsert(qualified).StartsWith("已保存"), "Qualified type fixture saved");
            query["recipeId"] = qualified.Id;
            Check((string)RecipeUiActions.PickSelection(query)["id"] == "3,11", "Fully qualified existing type hints remain compatible");
            var colorParam = new RecipeParam { Name = "color", Kind = "color", Default = "#4F83CC", Required = true };
            var colorRecipe = new Recipe { Code = "return color;", Params = new List<RecipeParam> { colorParam } };
            string colorError;
            Check(RecipeRunner.BuildCode(colorRecipe, new Dictionary<string,string> { ["color"] = "#ab12EF" }, out colorError).Contains("#ab12EF"), "Color picker generates string literal");
            foreach (var badColor in new[] { "red", "#abc", "#1234567", "#gg0000", "#123456;return 1;" })
                Check(RecipeRunner.BuildCode(colorRecipe, new Dictionary<string,string> { ["color"] = badColor }, out colorError) == null, "Invalid color rejected: " + badColor);
            Check(RecipeStore.ValidateParams(new List<RecipeParam> { new RecipeParam { Name = "value", Kind = "text", ObjectFilter = true } }) != null, "Only object parameters support query controls");
            colorParam.Default = "invalid";
            Check(RecipeStore.ValidateParams(colorRecipe.Params) != null, "Invalid default color rejected"); colorParam.Default = "#4F83CC";

            var registry = new ToolRegistry(); var save = new SaveRecipeTool(registry);
            Check(save.InputSchema["properties"]["actions"] != null && save.InputSchema["properties"]["params"]["items"]["properties"]["choices"] != null, "Authoring schema exposes buttons and choices");
            var input = JObject.Parse(@"{
              'name':'批量显示隐藏','description':'显示或隐藏选择的对象。','source_snippet':'tested_snippet','code':'return show ? mode : mode;',
              'params':[{'name':'show','label':'显示','kind':'bool','required':true},
                        {'name':'mode','label':'模式','kind':'text','required':true,'default':'all','choices':[{'label':'全部','value':'all'},{'label':'部分','value':'some'}]}],
              'actions':[{'id':'show','label':'显示','args':{'show':'true'}},{'id':'hide','label':'隐藏','args':{'show':'false'}}]
            }");
            Check(save.Execute(input).StartsWith("已保存"), "Save controls");
            var recipe = RecipeStore.All().Single(r => r.Name == "批量显示隐藏");
            Check(recipe.Name == (string)input["name"] && recipe.SourceSnippet == "tested_snippet", "Chinese title and source preserved");
            Check(recipe.Actions.Count == 2 && recipe.Params[1].Choices.Count == 2, "Controls persisted");
            ITxAgentTool tool;
            Check(registry.TryGet(Recipe.ToApiSafeName(recipe.Name), out tool) && tool.Name.All(c => c < 128), "API name remains safe");
            Check(tool.InputSchema["properties"]["__recipe_action"] != null, "Model can select same buttons");
            string error;
            var args = new Dictionary<string, string> { ["show"] = "true" };
            Check(RecipeRunner.BuildCode(recipe, args, "hide", out error).Contains("var show = false;"), "Host action overrides submitted value");
            Check(args["show"] == "true", "Caller arguments are not mutated");
            Check(RecipeRunner.BuildCode(recipe, args, "unknown", out error) == null && error != null, "Unknown action rejected");
            Check(RecipeRunner.BuildCode(recipe, new Dictionary<string, string>(), out error) == null, "Required bool needs explicit value without button");
            Check(RecipeRunner.BuildCode(recipe, new Dictionary<string, string> { ["mode"] = "other" }, "show", out error) == null, "Invalid choice rejected");
            Check(RecipeRunner.BuildCode(recipe, new Dictionary<string, string> { ["show"] = "invalid" }, out error) == null, "Invalid bool rejected");
            var export = RecipeStore.ExportMarkdown(recipe); Recipe imported;
            Check(RecipeStore.TryImportMarkdown(export, out imported, out error) && imported.Actions.Count == 2 && imported.Params[1].Choices.Count == 2, "Sharing round trip includes controls");
            Check(!RecipeStore.TryImportMarkdown(export.Replace("## 执行按钮", "## 执行按钮\ninvalid"), out imported, out error), "Malformed action section rejected");
            var unknown = export.Replace("\"show\": \"false\"", "\"missing\": \"false\"");
            Check(!RecipeStore.TryImportMarkdown(unknown, out imported, out error), "Unknown action parameter rejected on import");
            var objectParam = new RecipeParam { Name = "target", Kind = "object" };
            Check(RecipeStore.ValidateDefinition(new Recipe { Params = new List<RecipeParam> { objectParam }, Actions = new List<RecipeAction> { new RecipeAction { Id = "pick", Label = "选取", Args = new Dictionary<string,string> { ["target"] = "3,57,2,1" } } } }) != null, "Actions cannot persist object IDs");
            objectParam.Default = "3,57,2,1";
            Check(RecipeStore.ValidateParams(new List<RecipeParam> { objectParam }) != null, "Object defaults rejected");
            foreach (var value in new[] { "NaN", "Infinity", "1e999", "not-number" })
                Check(RecipeRunner.BuildCode(new Recipe { Code = "return x;", Params = new List<RecipeParam> { new RecipeParam { Name = "x", Kind = "number" } } }, new Dictionary<string,string> { ["x"] = value }, out error) == null, "Nonfinite numbers rejected: " + value);
            recipe.Lang = "python";
            Check(RecipeRunner.BuildCode(recipe, null, "hide", out error).Contains("show = False"), "Python buttons generate correct literals");
            recipe.Lang = "csharp";
            var badSave = (JObject)input.DeepClone(); badSave["name"] = "english_name";
            Check(save.Execute(badSave).Contains("中文"), "New titles must be Chinese");
            RecipeStore.RecordRun(recipe.Id, true);
            input["id"] = recipe.Id; input["name"] = "显示隐藏所选对象";
            Check(save.Execute(input).StartsWith("已保存"), "Rename updates original ID");
            var renamed = RecipeStore.Get(recipe.Id);
            Check(renamed.Name == "显示隐藏所选对象" && renamed.RunCount == 1, "Update preserves identity and history");
            Check(!registry.TryGet(Recipe.ToApiSafeName(recipe.Name), out tool), "Old registered name removed after successful save");
            var invalidUpdate = (JObject)input.DeepClone(); invalidUpdate["name"] = "无效的新标题"; invalidUpdate["params"][0]["name"] = "bad name";
            Check(save.Execute(invalidUpdate).StartsWith("Error:"), "Invalid update rejected");
            Check(registry.TryGet(Recipe.ToApiSafeName(renamed.Name), out tool), "Rejected update leaves existing tool registered");

            var colorInput = JObject.Parse(@"{'name':'色盘配方','description':'选择颜色后执行。','code':'return color;', 'params':[{'name':'color','kind':'color','default':'#4F83CC'}, {'name':'targets','kind':'objects','typeHint':'ITxDisplayableObject','objectFilter':true}]}" );
            Check(save.Execute(colorInput).StartsWith("已保存"), "Save schema accepts color and object query metadata");
            var savedColor = RecipeStore.All().Single(r => r.Name == "色盘配方");
            Recipe importedColor; string importedColorError;
            Check(RecipeStore.TryImportMarkdown(RecipeStore.ExportMarkdown(savedColor), out importedColor, out importedColorError)
                && importedColor.Params[0].Kind == "color" && importedColor.Params[1].ObjectFilter, "Color and query metadata round trip through sharing");
            int sceneReadsBeforeList = Tecnomatix.Engineering.TxApplication.ActiveDocument.PhysicalRoot.Reads;
            var list = RecipeUiActions.List();
            var visibilityRow = ((JArray)list["recipes"]).Single(x => (string)x["id"] == "default_visibility");
            Check((bool)visibilityRow["params"][0]["objectFilter"] && ((JArray)visibilityRow["params"][0]["objectTypes"]).Count == 0 && !(bool)visibilityRow["params"][0]["objectTypesLoaded"], "Recipe lists defer scene catalogs until controls are visible");
            Check(Tecnomatix.Engineering.TxApplication.ActiveDocument.PhysicalRoot.Reads == sceneReadsBeforeList, "Listing recipes does not traverse the scene");
            var catalogRequest = new JObject { ["recipeId"] = "default_visibility", ["param"] = "targets", ["study"] = "test-study" };
            var catalog = RecipeUiActions.ObjectTypes(catalogRequest);
            var values = ((JArray)catalog["objectTypes"]).Select(x => (string)x["value"]).ToList();
            Check((bool)catalog["ok"] && values.Count == 4 && values.Contains("Tecnomatix.Engineering.TxComponent") && values.Contains("Tecnomatix.Engineering.TxFrame"), "Catalog includes only actual compatible types across roots");
            Check(!values.Contains("Tecnomatix.Engineering.TxTool") && !values.Contains("Tecnomatix.Engineering.TestObject"), "Absent and incompatible types are excluded");
            var custom = new Tecnomatix.Engineering.TxFixtureCustom { Id = "3,16", Name = "New_Fixture" };
            var document = Tecnomatix.Engineering.TxApplication.ActiveDocument;
            document.ResourceRoot.Items.Add(custom);
            Check(((JArray)RecipeUiActions.ObjectTypes(catalogRequest)["objectTypes"]).Any(x => (string)x["value"] == custom.GetType().FullName), "New scene classes appear without a whitelist");
            var customQuery = new JObject { ["recipeId"] = "default_visibility", ["param"] = "targets", ["study"] = "test-study", ["search"] = true, ["objectType"] = custom.GetType().FullName };
            Check((string)RecipeUiActions.PickSelection(customQuery)["id"] == custom.Id, "New classes can be selected by their scene category");
            customQuery["objectType"] = fixtureArm.GetType().FullName;
            Check((int)RecipeUiActions.PickSelection(customQuery)["count"] == 2, "Concrete categories do not absorb derived categories");
            document.ResourceRoot.Items.Remove(custom);
            customQuery["objectType"] = custom.GetType().FullName;
            Check(!(bool)RecipeUiActions.PickSelection(customQuery)["ok"] && !((JArray)RecipeUiActions.ObjectTypes(catalogRequest)["objectTypes"]).Any(x => (string)x["value"] == custom.GetType().FullName), "Removed categories disappear and cannot select stale objects");
            var expectedLabels = new Dictionary<string,string> {
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
            var translatedObjects = new List<Tecnomatix.Engineering.ITxObject>();
            foreach (var entry in expectedLabels)
            {
                var type = typeof(Tecnomatix.Engineering.ITxObject).Assembly.GetType("Tecnomatix.Engineering." + entry.Key);
                var item = (Tecnomatix.Engineering.TestObject)Activator.CreateInstance(type);
                item.Id = "translated-" + entry.Key; item.Name = entry.Key; translatedObjects.Add(item);
            }
            document.ResourceRoot.Items.AddRange(translatedObjects);
            var translatedRows = (JArray)RecipeUiActions.ObjectTypes(catalogRequest)["objectTypes"];
            foreach (var entry in expectedLabels)
                Check(translatedRows.Any(x => (string)x["value"] == "Tecnomatix.Engineering." + entry.Key && (string)x["label"] == entry.Value), "Scene type translation " + entry.Key);
            Check(translatedRows.Select(x => (string)x["label"]).Distinct().Count() == translatedRows.Count, "Chinese type labels do not repeat");
            Check(translatedRows.Select(x => (string)x["value"]).Distinct().Count() == translatedRows.Count, "Scene catalog contains no duplicate type identities");
            var sameNameRobot = new OtherSceneLibrary.TxRobot { Id = "robot-other-library", Name = "Other_Robot" };
            document.ResourceRoot.Items.Add(sameNameRobot);
            var duplicateRows = (JArray)RecipeUiActions.ObjectTypes(catalogRequest)["objectTypes"];
            Check(duplicateRows.Select(x => (string)x["label"]).Distinct().Count() == duplicateRows.Count, "Duplicate names are disambiguated without losing types");
            Check(duplicateRows.Any(x => (string)x["value"] == sameNameRobot.GetType().FullName) && duplicateRows.Any(x => (string)x["value"] == robot.GetType().FullName), "Both original SDK identities survive duplicate Chinese labels");
            customQuery["objectType"] = sameNameRobot.GetType().FullName;
            Check((string)RecipeUiActions.PickSelection(customQuery)["id"] == sameNameRobot.Id, "Translated and disambiguated categories still select exact classes");
            document.ResourceRoot.Items.Remove(sameNameRobot);
            foreach (var item in translatedObjects) document.ResourceRoot.Items.Remove(item);
            catalogRequest["study"] = "other-study";
            Check(!(bool)RecipeUiActions.ObjectTypes(catalogRequest)["ok"], "Type requests cannot cross study boundaries");
            catalogRequest["study"] = "test-study";
            Tecnomatix.Engineering.TxApplication.ActiveDocument = null;
            Check(!(bool)RecipeUiActions.ObjectTypes(catalogRequest)["ok"], "No scene reports a readable catalog error");
            Tecnomatix.Engineering.TxApplication.ActiveDocument = document;
            var row = ((JArray)list["recipes"]).Single(x => (string)x["id"] == recipe.Id);
            Check((string)row["actions"][0]["label"] == "显示" && (string)row["params"][1]["choices"][0]["value"] == "all", "Both UIs receive same controls");
            var done = new ManualResetEventSlim(); JObject result = null;
            TxTools.Agent.Ps.PsBridge.Gate.Reset();
            var run = new JObject { ["recipeId"] = recipe.Id, ["study"] = "test-study", ["actionId"] = "hide", ["args"] = new JObject { ["show"] = "true" } };
            RecipeUiActions.Run(1, run, (seq, data) => { result = data; done.Set(); });
            Check(RecipeUiActions.IsRunning, "Shared execution gate held");
            Check(!(bool)RecipeUiActions.ObjectTypes(catalogRequest)["ok"], "Type scanning blocked during recipe execution");
            Check(((string)RecipeUiActions.PickSelection(new JObject { ["recipeId"] = "default_visibility", ["param"] = "targets" })["error"]).Contains("执行"), "Object query blocked during execution");
            JObject duplicate = null; RecipeUiActions.Run(2, run, (seq, data) => duplicate = data);
            Check(duplicate != null && !(bool)duplicate["ok"], "Double execution rejected");
            TxTools.Agent.Ps.PsBridge.Gate.Set();
            Check(done.Wait(TimeSpan.FromSeconds(5)) && (bool)result["ok"] && !RecipeUiActions.IsRunning, "Run completes and releases gate");
            Check(TxTools.Agent.Ps.PsBridge.Code.Contains("var show = false;"), "UI bridge applies authoritative action");
            Check(!UserPrefsStore.Load().RecipeArguments[recipe.Id].ContainsKey("target"), "Preferences contain only scalar arguments");
            Console.WriteLine("PASS: recipe controls, Chinese names, choices, actions, migrations, sharing and execution gate (" + checks + " checks)");
            RecipeSharingRegression.Main();
            return Environment.ExitCode;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { TxTools.Agent.Ps.PsBridge.Gate.Set(); }
    }
}
