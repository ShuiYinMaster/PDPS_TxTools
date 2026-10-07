using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TxTools.Agent.Core;

// Uses actual store/parser/parameter generator/compiler; never invokes engineering code.
public static class DefaultRecipesRegression
{
    static void Check(bool value,string reason){if(!value)throw new Exception(reason);}
    public static int Main(string[] args)
    {
        try {
            string sdk=Path.GetFullPath(args[0]);
            AppDomain.CurrentDomain.AssemblyResolve+=(sender,e)=>{
                string p=Path.Combine(sdk,new AssemblyName(e.Name).Name+".dll");
                return File.Exists(p)?Assembly.LoadFrom(p):null;
            };
            Verify(args.Length>1?args[1]:"");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
    static void Verify(string mode)
    {
        string folder=MdStore.FolderPath("recipes");
        Check(folder.Contains(Path.Combine("artifacts","default-recipes-regression")),"Test storage must be isolated");
        if(mode=="restart"){
            var all=RecipeStore.All();
            Check(all.Count==4,"Deleted default recreated on restart");
            Check(RecipeStore.Get("default_set_color").Name=="我的专用颜色","Custom default overwritten on restart");
            Check(RecipeStore.Get("default_number_objects").RunCount==1,"Run history overwritten on restart");
            Check(RecipeStore.Get("default_geometry_weld_points").RunCount==7,"Migrated history lost on restart");
            Console.WriteLine("PASS: fresh process preserves custom default, deletion and run history");return;
        }
        // Only our named fixture files are reset, inside the isolated output directory.
        foreach(string f in Directory.GetFiles(folder,"default_*.md"))File.Delete(f);
        string marker=Path.Combine(folder,".default-recipes-installed");if(File.Exists(marker))File.Delete(marker);
        var assembly=typeof(RecipeStore).Assembly;
        var resource=assembly.GetManifestResourceNames().Single(n=>n.EndsWith("default_set_color.md"));
        string definition;
        using(var stream=assembly.GetManifestResourceStream(resource))
        using(var reader=new StreamReader(stream))definition=reader.ReadToEnd();
        var doc=MarkdownDoc.Parse(definition);doc.Set("name","我的专用颜色");
        Check(MdStore.Write("recipes","default_set_color",doc),"Preexisting fixture write failed");
        string legacyDefinition;
        bool previousVersion=mode=="v2"||mode=="custom-v2";
        bool customized=mode=="custom"||mode=="custom-v2";
        using(var stream=assembly.GetManifestResourceStream("TxTools.Agent.LegacyRecipes.default_geometry_weld_points."+(previousVersion?"v2":"v1")+".md"))
        using(var reader=new StreamReader(stream))legacyDefinition=reader.ReadToEnd();
        var legacy=MarkdownDoc.Parse(legacyDefinition);legacy.Set("run_count",7);legacy.Set("fail_count",2);
        legacy.Set("created","2026-09-01 10:00:00");legacy.Set("last_run","2026-09-30 11:00:00");
        if(customized)legacy.Body=legacy.Body.Replace("## 参数","用户补充说明。\n\n## 参数");
        if(mode!="deleted")Check(MdStore.Write("recipes","default_geometry_weld_points",legacy),"Old geometry fixture write failed");
        File.WriteAllLines(marker,new[]{"default_geometry_weld_points"});
        int oldBackups=Directory.GetFiles(folder,"default_geometry_weld_points.md.before-keyword-*").Length;
        var recipes=RecipeStore.All();
        if(mode=="deleted"){
            Check(recipes.Count==4&&RecipeStore.Get("default_geometry_weld_points")==null,"Geometry deletion not preserved");
            Console.WriteLine("PASS: deleted geometry default not recreated by migration");return;
        }
        var geometry=RecipeStore.Get("default_geometry_weld_points");
        Check(geometry.RunCount==7&&geometry.FailCount==2&&geometry.LastRunUtc==DateTime.Parse("2026-09-30 11:00:00"),"Geometry migration lost metadata");
        if(customized){
            Check(geometry.Description.Contains("用户补充说明。")&&geometry.Params.Any(p=>p.Name==(previousVersion?"exclude_co2":"target_operation")),"Customized geometry default overwritten");
            Check(Directory.GetFiles(folder,"default_geometry_weld_points.md.before-keyword-*").Length==oldBackups,"Custom geometry backed up/changed");
            Console.WriteLine("PASS: custom geometry default not overwritten by migration");return;
        }
        Check(geometry.Params.Any(p=>p.Name=="name_keyword")&&!geometry.Params.Any(p=>p.Name=="target_operation"||p.Name=="exclude_co2"),"Old installed geometry default not upgraded");
        Check(Directory.GetFiles(folder,"default_geometry_weld_points.md.before-keyword-*").Length==oldBackups+1,"Upgrade original not backed up");
        Check(recipes.Count==5 && File.ReadAllLines(marker).Length==5,"Bundled defaults not seeded");
        Check(RecipeStore.Get("default_set_color").Name=="我的专用颜色","Existing default overwritten");
        foreach(var recipe in recipes){
            Check(RecipeStore.ValidateParams(recipe.Params)==null,"Invalid parameter schema "+recipe.Id);
            var values=new Dictionary<string,string>();
            foreach(var p in recipe.Params){
                Check((p.Kind!="object"&&p.Kind!="objects")||string.IsNullOrEmpty(p.Default),"Persisted object default");
                if(p.Kind=="object")values[p.Name]="3,57,2,3";
                if(p.Kind=="objects")values[p.Name]="3,57,2,1|3,57,2,2";
            }
            string error;
            Check(RecipeRunner.BuildCode(recipe,new Dictionary<string,string>(),out error)==null && !string.IsNullOrEmpty(error),"Required bindings accepted empty");
            for(int variant=0;variant<2;variant++){
                if(variant==1)foreach(var p in recipe.Params){
                    if(p.Kind=="bool")values[p.Name]=p.Default=="true"?"false":"true";
                    if(p.Name=="color_hex")values[p.Name]="#E45B5B";
                    if(p.Name=="name_prefix")values[p.Name]="测试_\"";
                    if(p.Name=="name_keyword")values[p.Name]="焊点_RsW\"";
                }
                string code=RecipeRunner.BuildCode(recipe,values,out error);
                Check(code!=null,error);
                var compiled=TxTools.Agent.Ps.CSharpRunner.Compile(code,out error);
                Check(compiled!=null,recipe.Name+" variant "+variant+": "+error);
            }
            foreach(var action in recipe.Actions){
                string code=RecipeRunner.BuildCode(recipe,values,action.Id,out error);
                Check(code!=null,error);
                Check(TxTools.Agent.Ps.CSharpRunner.Compile(code,out error)!=null,recipe.Name+" action "+action.Id+": "+error);
            }
            Console.WriteLine("PASS: "+recipe.Name+" parses, binds required objects, compiles against PS 2402 SDK / C# 5");
        }
        RecipeStore.RecordRun("default_number_objects",true);
        Check(RecipeStore.Delete("default_visibility"),"Default deletion failed");
        Console.WriteLine("PASS: actual embedded seeding and preservation; no engineering operations executed");
    }
}

// Dependencies unrelated to recipe persistence are isolated from the test.
namespace TxTools.Agent.Core {
    public class Snippet {public string Name,Description,Origin,Code;public int SuccessCount;}
    public static class SnippetStore {
        public static string NormalizeLang(string lang)=>lang=="python"?"python":"csharp";
        public static List<Snippet> All()=>new List<Snippet>();
        public static bool IsUnreliable(Snippet s)=>false;
    }
    public static class AuditLog {public static void Write(string line){Console.Error.WriteLine(line);} }
}
