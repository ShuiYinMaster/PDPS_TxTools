using System;
using System.Collections.Generic;
using Tecnomatix.Engineering;

// Executes the actual recipe bodies against an in-memory scene, never a PS scene.
public static class DefaultRecipeLogicRegression
{
    static void Check(bool ok,string reason){if(!ok)throw new Exception(reason);}
    static void Reject(Action action,string reason){try{action();}catch{ return;}throw new Exception(reason);}
    static void Log(string text){}
    static TxObjectList<ITxObject> Bind(params ITxObject[] items){var list=new TxObjectList<ITxObject>();list.AddRange(items);return list;}
    static TxSolid Solid(string id,string name,double x,double y,double z){return new TxSolid{Id=id,Name=name,Center=new TxVector(x,y,z),AbsoluteLocation=new TxTransformation{Translation=new TxVector(1,2,3),Orientation=42}};}
    public static int Main()
    {
        try {
            TxSolid a=Solid("a","DPUB-501021339-XWS_00.001_T1EJ-0000116179-RSW",100,200,300),
                b=Solid("b","DPUB-501021339-XWS_00.001_T1EJ-0000116180-RSW",100.03,200,300),
                c=Solid("c",a.Name,500,0,0),d=Solid("d","DPUB-501021339-XWS_00.001_T1EJ-0000116181-RSW",900,0,0);
            var group=new TxGroup{Id="g",Name="Markers"};group.Children.AddRange(new ITxObject[]{a,b,c,d});
            var nonSolid=new Tx2Or3DimensionalGeometry{Id="surface",Name="DPUB-501021339-X_T2-000000000000000000000999999999999-RSW",Center=new TxVector(700,0,0)};
            var co2=Solid("co2","DPUB-501021339-X_T1-00020-co2",double.NaN,0,0);
            var invalidName=Solid("bad-name","MissingStation",double.NaN,0,0);
            group.Children.AddRange(new ITxObject[]{nonSolid,co2,invalidName});
            var target=new TxOperationRoot{Id="op",Name="Root"};
            TxApplication.ActiveDocument=new TxDocument{OperationRoot=target};
            target.Children.Add(new TxWeldLocationOperation{Id="existing",Name="Existing",AbsoluteLocation=new TxTransformation{Translation=new TxVector(900,0,0)}});
            string result=(string)DefaultScripts.default_geometry_weld_points(Log,Bind(group,a)," rsw ",.1);
            Check(target.CreateCalls==2 && target.Children.Count==3,"Duplicate scope/position filtering failed");
            Check(target.Children.Exists(o=>o.Name=="T1EJ_501021339_116179")&&target.Children.Exists(o=>o.Name=="T2_501021339_999999999999"),"DPUB naming or long numeric ID normalization failed");
            Check(result.Contains("关键字不匹配跳过：2")&&result.Contains("同名异位跳过：1"),"Skip/conflict reporting failed");
            var location=(TxWeldLocationOperation)target.Children.Find(o=>o.Name=="T1EJ_501021339_116179");
            Check(location.AbsoluteLocation.Translation.X==100 && location.AbsoluteLocation.Translation.Y==200 && location.AbsoluteLocation.Translation.Z==300,"Used origin instead of geometric center");
            Check(location.AbsoluteLocation.Orientation==17,"Did not retain GeometricCenter transform");
            Check(location.WeldPoint.AbsoluteLocation.Translation.X==100,"Feature and operation positions differ");
            DefaultScripts.default_geometry_weld_points(Log,Bind(group),"RSW",.1);
            Check(target.CreateCalls==2 && target.Children.Count==3,"Repeated run created duplicates");
            var included=new TxOperationRoot{Id="op2",Name="Included"};TxApplication.ActiveDocument.OperationRoot=included;
            co2.Center=new TxVector(1100,0,0);
            DefaultScripts.default_geometry_weld_points(Log,Bind(co2),"CO2",0);
            Check(included.Children[0].Name=="T1_501021339_20","Explicit CO2 keyword still excluded");
            var zero=Solid("zero","DPUB-501021339-X_T1-00000-RSW",1200,0,0);
            DefaultScripts.default_geometry_weld_points(Log,Bind(zero),"",0);
            Check(included.Children[1].Name=="T1_501021339_0","All-zero ID normalization failed");
            var arbitrary=Solid("arbitrary","焊点标记_RsW_42",1300,0,0);
            DefaultScripts.default_geometry_weld_points(Log,Bind(arbitrary),"rsw",0);
            Check(included.Children[2].Name==arbitrary.Name,"Non-DPUB matching marker was rejected or renamed");
            invalidName.Center=new TxVector(1400,0,0);
            DefaultScripts.default_geometry_weld_points(Log,Bind(invalidName),"",0);
            Check(included.Children[3].Name==invalidName.Name,"Blank keyword did not include arbitrary name");
            var unnamed=Solid("unnamed","",1500,0,0);
            DefaultScripts.default_geometry_weld_points(Log,Bind(unnamed)," ",0);
            Check(included.Children[4].Name=="WP_001","Unnamed marker fallback failed");
            int beforeNoMatch=included.CreateCalls;
            string noMatch=(string)DefaultScripts.default_geometry_weld_points(Log,Bind(arbitrary),"does-not-exist",0);
            Check(included.CreateCalls==beforeNoMatch&&noMatch.Contains("关键字不匹配跳过：1"),"No-match run mutated scene or omitted report");
            DefaultScripts.default_geometry_weld_points(Log,Bind(arbitrary),"*",0);
            Check(included.CreateCalls==beforeNoMatch,"Keyword was treated as wildcard");
            var invalid=new TxOperationRoot{Id="op3",Name="Invalid"};TxApplication.ActiveDocument.OperationRoot=invalid;
            Reject(()=>DefaultScripts.default_geometry_weld_points(Log,Bind(a),"RSW",-1),"Negative tolerance accepted");
            var bad=Solid("bad","DPUB-501021339-X_T1-42-RSW",double.NaN,0,0);
            Reject(()=>DefaultScripts.default_geometry_weld_points(Log,Bind(a,bad),"RSW",.1),"Invalid center accepted");
            Check(invalid.CreateCalls==0,"Invalid input changed scene before validation");
            var failing=new TxOperationRoot{Id="op4",Name="Failure",FailDispose=true};TxApplication.ActiveDocument.OperationRoot=failing;
            Reject(()=>DefaultScripts.default_geometry_weld_points(Log,Bind(a),"RSW",.1),"Creation failure reported successful");
            Check(failing.Children.Count==0 && failing.LastFeature.Deleted,"Failure left a location or manufacturing feature");
            var denied=new TxOperationRoot{Id="op5",Name="Denied",AllowCreate=false};TxApplication.ActiveDocument.OperationRoot=denied;
            Reject(()=>DefaultScripts.default_geometry_weld_points(Log,Bind(a),"RSW",.1),"Creator rejection accepted");
            Check(denied.CreateCalls==0,"Creator rejection still mutated scene");
            TxApplication.ActiveDocument=null;
            Reject(()=>DefaultScripts.default_geometry_weld_points(Log,Bind(a),"RSW",.1),"No-document guard failed");
            Console.WriteLine("PASS: geometry recipe: keyword matching, arbitrary/unnamed names, blank keyword, explicit CO2, no matches, root creation, deduplication, center transform, preflight and cleanup");

            DefaultScripts.default_set_color(Log,Bind(group,a),"#E45B5B",true);
            Check(a.Color.R==0xE4 && a.Color.G==0x5B && a.Color.B==0x5B && a.ColorSets==1,"Color or overlap deduplication failed");
            int before=a.ColorSets;Reject(()=>DefaultScripts.default_set_color(Log,Bind(a),"red",false),"Invalid hex accepted");Check(a.ColorSets==before,"Invalid color mutated scene");
            DefaultScripts.default_visibility(Log,Bind(group,a),false,true);
            Check(!a.Visible && a.BlankCalls==1,"Hide or overlap deduplication failed");
            DefaultScripts.default_visibility(Log,Bind(a),true,false);Check(a.Visible,"Show failed");
            string nameA=a.Name,nameB=b.Name;
            Reject(()=>DefaultScripts.default_number_objects(Log,Bind(a,b),"Part_",2.5,3),"Fractional numbering accepted");
            Reject(()=>DefaultScripts.default_number_objects(Log,Bind(a,b),"Part_",int.MaxValue,3),"Number overflow accepted");
            Check(a.Name==nameA && b.Name==nameB,"Invalid numbering changed names");
            DefaultScripts.default_number_objects(Log,Bind(a,b,a),"Part_",1,3);
            Check(a.Name=="Part_001" && b.Name=="Part_002","Numbering order/deduplication failed");
            string center=(string)DefaultScripts.default_object_coordinates(Log,Bind(a),true);
            string origin=(string)DefaultScripts.default_object_coordinates(Log,Bind(a),false);
            Check(center.Contains("100.000 | 200.000 | 300.000") && origin.Contains("1.000 | 2.000 | 3.000"),"Coordinate report mode failed");
            Console.WriteLine("PASS: real color/visibility/number/coordinates recipe logic and invalid input guards");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}

namespace Tecnomatix.Engineering
{
    public interface ITxObject{string Id{get;}string Name{get;set;}bool CanBeRenamed{get;}}
    public interface ITxObjectCollection:ITxObject{List<ITxObject> GetAllDescendants(TxTypeFilter filter);}
    public interface ITxDisplayableObject:ITxObject{TxColor Color{get;set;}void Display();void Blank();}
    public interface ITxLocatableObject:ITxObject{TxTransformation GeometricCenter{get;}TxTransformation AbsoluteLocation{get;set;}}
    public interface ITxWeldLocationOperationCreation{bool CanCreateWeldLocationOperation(TxWeldLocationOperationCreationData d);TxWeldLocationOperation CreateWeldLocationOperation(TxWeldLocationOperationCreationData d);}
    public class TxObject:ITxObject{public string Id{get;set;}public string Name{get;set;}public bool CanBeRenamed{get;set;}=true;public bool Deleted;public virtual void Delete(){Deleted=true;}public bool CanBeDeleted{get{if(Deleted)throw new TxInvalidObjectException();return true;}}}
    public class TxDisplay:TxObject,ITxDisplayableObject{TxColor color;public int ColorSets,BlankCalls;public bool Visible=true;public TxColor Color{get{return color;}set{color=value;ColorSets++;}}public void Display(){Visible=true;}public void Blank(){Visible=false;BlankCalls++;}}
    public class TxGroup:TxDisplay,ITxObjectCollection{
        public List<ITxObject> Children=new List<ITxObject>();
        public List<ITxObject> GetAllDescendants(TxTypeFilter f){var result=new List<ITxObject>();foreach(var c in Children){if(f.Type.IsInstanceOfType(c))result.Add(c);var group=c as ITxObjectCollection;if(group!=null)result.AddRange(group.GetAllDescendants(f));}return result;}
    }
    public class Tx2Or3DimensionalGeometry:TxDisplay,ITxLocatableObject{public TxVector Center;public TxTransformation GeometricCenter{get{return new TxTransformation{Translation=Center,Orientation=17};}}public TxTransformation AbsoluteLocation{get;set;}}
    public class TxSolid:Tx2Or3DimensionalGeometry{}
    public static class TxApplication{public static TxDocument ActiveDocument;}
    public class TxDocument{public TxOperationRoot OperationRoot;}
    public class TxWeldPoint:TxObject{TxTransformation pose;public bool FailSet;public TxTransformation AbsoluteLocation{get{return pose;}set{if(FailSet)throw new Exception("Injected feature setter failure");pose=value;}}}
    public class TxWeldLocationOperation:TxObject,ITxLocatableObject{public TxWeldPoint WeldPoint;public Action DeleteAction;public TxTransformation AbsoluteLocation{get;set;}public TxTransformation GeometricCenter{get{return AbsoluteLocation;}}public override void Delete(){base.Delete();if(DeleteAction!=null)DeleteAction();}}
    public class TxOperationRoot:TxGroup,ITxWeldLocationOperationCreation{
        public bool AllowCreate=true,FailDispose;public int CreateCalls;public TxWeldPoint LastFeature;
        public bool CanCreateWeldLocationOperation(TxWeldLocationOperationCreationData d){throw new NotImplementedException("Do not use CanCreate precheck in reference workflow");}
        public TxWeldLocationOperation CreateWeldLocationOperation(TxWeldLocationOperationCreationData d){if(!AllowCreate)throw new Exception("Injected rejection");CreateCalls++;LastFeature=new TxWeldPoint{Id="f"+CreateCalls,Name=d.Name,AbsoluteLocation=new TxTransformation{Translation=(TxVector)d.WeldPointCreationData}};var loc=new TxWeldLocationOperation{Id="loc"+CreateCalls,Name=d.Name,WeldPoint=LastFeature,AbsoluteLocation=d.ProjectedLocation};loc.DeleteAction=()=>Children.Remove(loc);Children.Add(loc);return loc;}
    }
    public class TxTypeFilter{public Type Type;public TxTypeFilter(Type t){Type=t;}}
    public class TxObjectList<T>:List<T>{}
    public class TxVector{public double X,Y,Z;public TxVector(double x,double y,double z){X=x;Y=y;Z=z;}}
    public class TxTransformation{public TxVector Translation=new TxVector(0,0,0);public int Orientation;public bool IsMirrored;public bool IsValid=true,IsRigidBodyTransformation=true;public TxTransformation(){}public TxTransformation(TxTransformation p){Translation=p.Translation;Orientation=p.Orientation;IsMirrored=p.IsMirrored;IsValid=p.IsValid;IsRigidBodyTransformation=p.IsRigidBodyTransformation;}}
    public struct TxColor{public byte R,G,B;public TxColor(byte r,byte g,byte b){R=r;G=g;B=b;}}
    public class TxWeldLocationOperationCreationData:IDisposable{public string Name;public TxTransformation ProjectedLocation;public object WeldPointCreationData;public void Dispose(){if(TxApplication.ActiveDocument.OperationRoot.FailDispose)throw new Exception("Injected disposal failure");}}
    public class TxInvalidObjectException:Exception{}
}
namespace Tecnomatix.Engineering.DataTypes{public static class TxMfgCreationDataFactory{public static object CreateWeldPointCreationData(string name,Tecnomatix.Engineering.TxVector p){return p;}}}
