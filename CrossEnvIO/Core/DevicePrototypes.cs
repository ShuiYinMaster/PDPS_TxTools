using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Tecnomatix.Engineering;
using Tecnomatix.Engineering.DataTypes;
using Tecnomatix.Engineering.Utilities;
using Tecnomatix.Planning;
using TxTools.Agent.Core;

namespace TxTools.CrossEnvIO
{
    internal static class DevicePrototypes
    {
        // PS 2402 exposes the DTO's getters but its raw-data constructor is internal.
        // Construct this data object directly; CreatePrototypeData re-queries the
        // receiver's planning schema and can reject valid Type/Family read from TuneData.
        private static readonly ConstructorInfo DataConstructor=typeof(TxTunePmDocumentPrototypeData).GetConstructor(
            BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,
            new[]{typeof(string),typeof(string),typeof(string),typeof(double)},null);

        private static string KnownFamily(string type)
        {
            switch(type)
            {
                case "PmPartPrototype":case "PartPrototype":return "PartPrototype";
                case "PmToolPrototype":case "ToolPrototype":return "ToolPrototype";
                case "PmEquipmentPrototype":case "EquipmentPrototype":return "EquipmentPrototype";
                default:return null;
            }
        }
        private static string FamilyFor(DevicePrototypeData source)=>string.IsNullOrWhiteSpace(source.Family)?KnownFamily(source.Type):source.Family;
        internal static bool Required(string type)
        {
            var sdkType = typeof(ITxObject).Assembly.GetType(type.StartsWith("Tecnomatix.", StringComparison.Ordinal) ? type : "Tecnomatix.Engineering." + type, false);
            return sdkType != null && typeof(TxDevice).IsAssignableFrom(sdkType);
        }
        internal static DevicePrototypeData Capture(ITxObject obj, string cojt)
        {
            if (string.IsNullOrWhiteSpace(cojt)) return null;
            var result = new DevicePrototypeData { RuntimeType = obj.GetType().Name };
            try
            {
                TxTunePmDocumentPrototypeData data = null;
                try { data = new TxTunePmDocumentManager().LoadPrototypeDataFromDocument(cojt); }
                catch (TxLoadPrototypeDataFromDocumentFailedException) { /* Fall back to the live source prototype. */ }
                if(data!=null)
                { result.Type=data.Type; result.Family=data.Family; result.ExternalId=data.ExternalId; result.Weight=data.Weight; }
                if(string.IsNullOrWhiteSpace(result.Type))
                {
                    var planning = (obj as ITxProcessModelObject)?.PlanningRepresentation;
                    var prototype = (planning as ITxPlanningInstance)?.Prototype ?? planning as ITxPlanningPrototype;
                    result.Type = prototype?.PlanningType;
                }
                if(!string.IsNullOrWhiteSpace(result.Type)&&string.IsNullOrWhiteSpace(result.Family))
                {
                    result.Family=KnownFamily(result.Type);
                    // Only a missing SOURCE family needs source-side schema lookup.
                    // Complete TuneData records never depend on planning metadata.
                    if(result.Family==null)result.Family=new TxTunePmDocumentManager().CreatePrototypeData(result.Type,result.Weight).Family;
                }
                if (!string.IsNullOrWhiteSpace(result.Type)&&!string.IsNullOrWhiteSpace(result.Family)) return result;
                result.Error = "源端设备没有可读取的 Planning 原型定义，请先在源工程定义设备后重新捕获";
            }
            catch (Exception ex) { result.Error = "读取源端设备原型失败: " + ex.Message; }
            return Required(result.RuntimeType)||!string.IsNullOrWhiteSpace(result.Type)||!string.IsNullOrWhiteSpace(result.Family) ? result : null;
        }
        internal static void Validate(TransferPackage package)
        {
            var source = package.Prototype;
            if (source == null)
            {
                if (package.Types.Keys.Any(Required)) throw new InvalidOperationException("设备数据缺少源端原型信息，请用新版插件重新导出: " + package.Name);
                return;
            }
            if (!string.IsNullOrEmpty(source.Error)) throw new InvalidOperationException(package.Name + ": " + source.Error);
            if (string.IsNullOrWhiteSpace(source.Type) || double.IsNaN(source.Weight) || double.IsInfinity(source.Weight))
                throw new InvalidOperationException("无效设备原型信息: " + package.Name);
            if(string.IsNullOrWhiteSpace(FamilyFor(source)))
                throw new InvalidOperationException("源端原型缺少 Family，请从源工程重新捕获完整原型记录："+package.Name+" / Type="+source.Type);
            if (!(TxApplication.ActiveDocument.PlatformGlobalServicesProvider is ITxOfflineGlobalServicesProvider))
                throw new InvalidOperationException("设备原型传输当前仅支持离线工程: " + package.Name);
            // The source record already supplies Type and Family. No receiver-side
            // planning metadata query or descriptor allocation is needed in preflight.
        }
        internal static TxTunePmDocumentPrototypeData CreateTargetData(TransferPackage package)
        {
            var source=package.Prototype;
            if(DataConstructor==null)
            {
                throw new InvalidOperationException("当前 SDK 不提供已知的原型数据构造接口；未修改模型。\n模型："+package.Name
                    +"\nType="+source.Type+"；Family="+FamilyFor(source));
            }
            try
            {
                return (TxTunePmDocumentPrototypeData)DataConstructor.Invoke(new object[]{Guid.NewGuid().ToString(),source.Type,FamilyFor(source),source.Weight});
            }
            catch(Exception ex)
            {
                var cause=ex is TargetInvocationException&&ex.InnerException!=null?ex.InnerException:ex;
                throw new InvalidOperationException("构造原型数据对象失败："+package.Name+"\nType="+source.Type+"；Family="+FamilyFor(source)
                    +"\n"+cause.GetType().Name+"："+cause.Message,cause);
            }
        }
        private static bool Same(TxTunePmDocumentPrototypeData data, DevicePrototypeData source) => data != null
            && data.Type == source.Type && data.Family==FamilyFor(source) && data.Weight == source.Weight;
        internal static void Prepare(TransferPlan plan, Action<string> log)
        {
            foreach (var package in plan.Packages.Where(p => p.Conflict != TransferConflict.Skip && p.Prototype != null))
            {
                Validate(package);
                string path = plan.Destination(package), tune = Path.Combine(path, "TuneData.xml");
                var source = package.Prototype; var manager = new TxTunePmDocumentManager();
                TxTunePmDocumentPrototypeData existing = null;
                try { existing=manager.LoadPrototypeDataFromDocument(path); }
                catch (TxLoadPrototypeDataFromDocumentFailedException ex)
                { log("[原型] 无法读取当前接收工程记录，将备份并尝试创建: "+package.Name+" / "+ex.Message); }
                if (Same(existing, source)) { log("[原型] 复用目标工程定义: " + package.Name + " / " + source.Type); continue; }
                byte[] original = File.Exists(tune) ? File.ReadAllBytes(tune) : null;
                string backup = null;
                if (original != null)
                {
                    backup = Path.Combine(TxToolsTemp.SessionDirectory("CrossEnvIO", "prototype-backup"), "TuneData.xml");
                    File.WriteAllBytes(backup, original);
                }
                try
                {
                    // External IDs are project-local. Allocate a new receiving ID, never reuse a source planning object.
                    // Keep both persisted type fields, including custom types such as
                    // Fence / ToolPrototype. Do not replace them with a base class.
                    var target = CreateTargetData(package);
                    manager.SavePrototypeDataToDocument(path, target);
                    var saved=manager.LoadPrototypeDataFromDocument(path);
                    if (!Same(saved, source)||saved.ExternalId!=target.ExternalId) throw new IOException("写入后原型回读不一致：Type、Family、重量或目标 ExternalID 未正确保存");
                    PrototypeReceipts.Save(plan, package, PackageFiles.Inspect(path).Hash);
                    log("[原型] 已为接收工程建立 " + source.Type + " / " + package.Name + (backup == null ? "" : "；原 TuneData 备份: " + backup));
                }
                catch
                {
                    if (original != null) File.WriteAllBytes(tune, original); else if (File.Exists(tune)) File.Delete(tune);
                    throw;
                }
            }
        }
    }
}
