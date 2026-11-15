using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Tecnomatix.Engineering;
using TxTools.Agent.Core;

namespace TxTools.CrossEnvIO
{
    internal static class TransferRuntime
    {
        internal const int LibraryProtocol=6;
        internal static TransferJobs Jobs => new TransferJobs(TxToolsTemp.DirectoryFor("CrossEnvIO", "jobs"));

        internal static JObject Describe()
        {
            var doc = TxApplication.ActiveDocument ?? throw new InvalidOperationException("没有活动工程");
            return new JObject { ["protocol"] = 2, ["durable_library"] = true, ["native_library"] = true, ["library_subfolders"] = true, ["library_save_version"] = LibraryProtocol, ["library_insert_mode"] = "guarded_appfunc_a", ["named_roots"] = true, ["psz_save_reload"] = true, ["json_runtime"] = typeof(JToken).Assembly.GetName().Version.ToString(), ["study"] = doc.CurrentStudy.Name,
                ["document"] = doc.FinalDestination ?? "", ["study_id"] = TransferCapture.StudyId(),
                ["systemRoot"] = TxApplication.SystemRootDirectory, ["sdk_runtime"] = typeof(ITxComponent).Assembly.GetName().Version.ToString(),
                ["unicode_insert_profile"] = AppFuncCompatibility.ProfileName,["frame_alignment"]=true,["optional_weld_binding"]=true,["weld_with_models"]=true };
        }

        internal static void ValidateTarget(TransferPlan plan)
        {
            if (plan.Version != 2) throw new InvalidDataException("不支持的传输协议版本，请用新版插件重新检查");
            var current = Describe();
            if (plan.TargetStudy != (string)current["study"] || plan.TargetDocument != (string)current["document"]
                || plan.TargetStudyId != (string)current["study_id"] || !TransferPaths.Same(plan.TargetRoot, (string)current["systemRoot"]))
                throw new InvalidOperationException("接收端工程、研究标识或库根已变化，请重新确认并预检");
        }

        internal static List<ITxObject> SceneObjects(bool includeOperations = false)
        {
            var result = new List<ITxObject>();
            var pending = new Stack<ITxObject>(); pending.Push(TxApplication.ActiveDocument.PhysicalRoot);
            if (includeOperations) pending.Push(TxApplication.ActiveDocument.OperationRoot);
            var seen = new HashSet<string>();
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (!seen.Add(current.Id)) continue;
                result.Add(current);
                if (!(current is TxPhysicalRoot) && !(current is TxCompoundPart) && !(current is TxCompoundResource)
                    && !(includeOperations && (current is TxCompoundOperation || current == TxApplication.ActiveDocument.OperationRoot))) continue;
                var collection = current as ITxObjectCollection;
                if (collection != null) foreach (ITxObject child in SceneAccess.Children(collection)) if (child != null) pending.Push(child);
            }
            return result;
        }

        internal static ITxObject Parent(string id)
        {
            var doc = TxApplication.ActiveDocument;
            if (string.IsNullOrEmpty(id) || id == doc.PhysicalRoot.Id) return doc.PhysicalRoot;
            var parent = SceneObjects().FirstOrDefault(o => o.Id == id);
            if (!(parent is TxCompoundPart) && !(parent is TxCompoundResource))
                throw new InvalidOperationException("接收父容器已失效或不是可创建模型结构的容器");
            return parent;
        }

        internal static string SceneStamp()
        {
            var sb = new StringBuilder();
            foreach (var obj in SceneObjects(true).OrderBy(o => o.Id, StringComparer.Ordinal))
            {
                sb.Append(obj.Id).Append('\t').Append(obj.Name).Append('\t').Append(obj.GetType().Name);
                if (obj is TxPhysicalRoot || obj is TxCompoundPart || obj is TxCompoundResource
                    || obj is TxCompoundOperation || obj == TxApplication.ActiveDocument.OperationRoot)
                    foreach (ITxObject child in SceneAccess.Children(obj)) sb.Append('|').Append(child.Id);
                if (!(obj is TxPhysicalRoot))
                {
                    sb.Append('|').Append(StructureIO.GetCojtPath(obj));
                    dynamic located = obj;
                    try
                    {
                        var location = located.AbsoluteLocation as TxTransformation;
                        foreach (double value in TxMath.TxToArr(location)) sb.Append('|').Append(value.ToString("R", CultureInfo.InvariantCulture));
                    }
                    catch { sb.Append("|unlocated"); }
                }
                if (obj is TxWeldLocationOperation weld && weld.WeldPoint != null)
                {
                    sb.Append("|weld:").Append(weld.WeldPoint.Name);
                    foreach (ITxObject part in weld.WeldPoint.AssignedParts) sb.Append('|').Append(part.Id);
                    sb.Append("|leading:").Append((weld.WeldPoint.LeadingPart as ITxObject)?.Id);
                }
                sb.AppendLine();
            }
            return PackageFiles.HashBytes(Encoding.UTF8.GetBytes(sb.ToString()));
        }

        internal static JObject Preflight(TransferPlan plan)
        {
            ValidateTarget(plan);
            if (plan.Kind != "model" && plan.Kind != "weld") throw new InvalidDataException("未知传输类型");
            var errors = new List<string>();
            var bindings = new JArray();
            try
            {
                if(plan.SourceFrame!=null)TxMath.FromWorldValues(plan.SourceFrame);
                if(plan.TargetFrame!=null)TxMath.FromWorldValues(plan.TargetFrame);
            }
            catch(Exception ex){errors.Add("双端参考坐标无效："+ex.Message);}
            if (!File.Exists(plan.DataFile) || PackageFiles.HashFile(plan.DataFile) != plan.DataHash)
                errors.Add("数据文件缺失或发生变化，请重新捕获传输范围");
            else if (plan.Kind == "weld")
            {
                ISet<string> incoming=null;
                if(plan.IncludeWeldParts)
                {
                    var models=plan.WeldModelsPlan();
                    if(models==null)errors.Add("没有绑定零件的模型快照，请从源端重新捕获或导入包含零件的离线包。");
                    else
                    {
                        var modelCheck=Preflight(models);
                        foreach(var error in modelCheck["errors"] as JArray??new JArray())errors.Add("绑定零件："+(string)error);
                        if((bool?)modelCheck["ok"]==true)
                        {
                            var table=TransferTable.Read(models.DataFile);
                            incoming=new HashSet<string>(table.Rows.Where(r=>StructureIO.IsInsertable(table.Value(r,"Type"))
                                &&(string.IsNullOrWhiteSpace(table.Value(r,"COJT"))||models.Packages.Any(p=>p.Conflict!=TransferConflict.Skip
                                    &&TransferPaths.Same(p.OriginalSource,table.Value(r,"COJT"))))).Select(r=>table.Value(r,"SourceObjectId")),StringComparer.Ordinal);
                        }
                    }
                }
                foreach (var issue in WeldPointIO.Preflight(plan.DataFile, plan.Project, plan.BindOnly, plan.ObjectMap, plan.WeldBindings,plan.AllowUnboundWelds,incoming))
                { errors.Add(issue.Value); bindings.Add(new JObject { ["key"] = issue.Key, ["message"] = issue.Value }); }
            }
            else
            {
                try{AppFuncCompatibility.Current.EnsureIdle();}catch(Exception ex){errors.Add(ex.Message);}
                foreach (var package in plan.Packages.Where(p => p.Conflict != TransferConflict.Skip))
                    try { DevicePrototypes.Validate(package); } catch (Exception ex) { errors.Add(ex.Message); }
                var table = TransferTable.Read(plan.DataFile);
                var packages = new HashSet<string>(plan.Packages.Select(p => Path.GetFullPath(p.OriginalSource)), StringComparer.OrdinalIgnoreCase);
                var filePackages = new HashSet<string>(table.Rows.Select(r => table.Value(r, "COJT")).Where(s => !string.IsNullOrWhiteSpace(s)).Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
                if (!packages.SetEquals(filePackages)) errors.Add("组件映射与数据中的 COJT 清单不一致");
                var paths = new HashSet<string>(StringComparer.Ordinal);
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in table.Rows)
                {
                    string id = table.Value(row, "SourceObjectId");
                    if (id.Length > 0 && !ids.Add(id)) errors.Add("源对象标识重复: " + id);
                }
                var scene = SceneObjects();
                var parent = Parent(plan.TargetParentId);
                foreach (var row in table.Rows)
                {
                    string type = table.Value(row, "Type"), name = table.Value(row, "Name"), own = table.Value(row, "ParentPath");
                    try{StructureIO.ReadPose(row,Array.IndexOf(table.Header,"WorldTransform"));}
                    catch(Exception ex){errors.Add("完整世界位姿无效："+own+" / "+ex.Message);}
                    foreach (string field in new[] { "X", "Y", "Z", "RX", "RY", "RZ" })
                    {
                        double number;
                        if (!double.TryParse(table.Value(row, field), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                            || double.IsNaN(number) || double.IsInfinity(number)) errors.Add("无效位姿: " + own + " / " + field);
                    }
                    string sourceParent = table.Value(row, "SourceParentId");
                    if (sourceParent.Length > 0 && !ids.Contains(sourceParent)) errors.Add("源父对象不在数据范围中: " + own);
                    if (sourceParent.Length > 0)
                    {
                        var parentRow = table.Rows.FirstOrDefault(r => table.Value(r, "SourceObjectId") == sourceParent);
                        if (parentRow != null && table.Value(parentRow, "ParentPath").Count(c => c == '/') >= own.Count(c => c == '/'))
                            errors.Add("源父子层级顺序无效: " + own);
                    }
                    if (!paths.Add(own)) errors.Add("存在重复对象路径: " + own);
                    string source = table.Value(row, "COJT");
                    var package = string.IsNullOrWhiteSpace(source) ? null : plan.Packages.FirstOrDefault(p => TransferPaths.Same(p.OriginalSource, source));
                    if (package?.Conflict == TransferConflict.Skip) continue;
                    if (type.Contains("PhysicalRoot")) continue;
                    if (type != "TxCompoundPart" && type != "TxCompoundResource" && package == null
                        && scene.Count(o => o.Name == name && o.GetType().Name == type) != 1)
                        errors.Add("缺少可复制组件或唯一现有对象: " + own + " (" + type + ")");
                    if (type == "TxCompoundPart" && !(parent is ITxCompoundPartCreation))
                        errors.Add("目标父容器不支持创建零件结构: " + parent.Name);
                    if (type == "TxCompoundResource" && !(parent is ITxCompoundResourceCreation))
                        errors.Add("目标父容器不支持创建资源结构: " + parent.Name);
                    if (type.Contains("Compound") && type != "TxCompoundPart" && type != "TxCompoundResource")
                        errors.Add("无法直接重建此类容器: " + type);
                    // 结构 TSV 未包含设备关节和工艺属性；未支持的对象不冒充组件插入。
                    if (package != null && type != "TxCompoundPart" && type != "TxCompoundResource" && !StructureIO.IsInsertable(type))
                        errors.Add("该原生类型尚未提供重建能力，请传输其归属组件: " + type + " / " + name);
                }
                CheckExistingTargets(plan, table, parent, errors);
            }
            return new JObject { ["ok"] = errors.Count == 0, ["durable_library"] = true, ["native_library"] = true, ["library_subfolders"] = true, ["library_save_version"] = LibraryProtocol, ["library_insert_mode"] = "guarded_appfunc_a", ["named_roots"] = true, ["psz_save_reload"] = true,
                ["save_reload_required"] = false, ["errors"] = JArray.FromObject(errors.Distinct()),
                ["bindings"] = bindings, ["frame_alignment"]=true,["optional_weld_binding"]=true,["weld_with_models"]=true,["scene_stamp"] = SceneStamp() };
        }

        private static void CheckExistingTargets(TransferPlan plan, TransferTable table, ITxObject targetRoot, List<string> errors)
        {
            var bySource = new Dictionary<string, ITxObject>(StringComparer.Ordinal);
            var byPath = new Dictionary<string, ITxObject>(StringComparer.Ordinal);
            var rootNames=new Dictionary<string,List<string>>(StringComparer.Ordinal);
            bool compatibilityChecked=false;string compatibilityIssue=null;
            foreach (var row in table.Rows.OrderBy(r => table.Value(r, "ParentPath").Count(c => c == '/')))
            {
                string id = table.Value(row, "SourceObjectId"), own = table.Value(row, "ParentPath"), type = table.Value(row, "Type"), name = table.Value(row, "Name");
                if(!string.IsNullOrWhiteSpace(table.Value(row,"TargetName")))name=table.Value(row,"TargetName");
                if (type.Contains("PhysicalRoot"))
                { if (id.Length > 0) bySource[id] = targetRoot; byPath[own] = targetRoot; byPath["R:" + own] = targetRoot; byPath["P:" + own] = targetRoot; continue; }
                ITxObject parent = targetRoot;
                string parentId = table.Value(row, "SourceParentId");
                if(string.IsNullOrEmpty(parentId)&&own.IndexOf('/')<0)
                {
                    if(!rootNames.TryGetValue(name,out var types))rootNames[name]=types=new List<string>();
                    if(types.Any(t=>!(t=="TxCompoundPart"&&type=="TxCompoundResource"||t=="TxCompoundResource"&&type=="TxCompoundPart")))
                        errors.Add("多个导出根映射到同一个目标名称："+name+"。请重新捕获，使同名根使用独立目标名称。");
                    types.Add(type);
                }
                if (parentId.Length > 0) bySource.TryGetValue(parentId, out parent);
                else { int slash = own.LastIndexOf('/'); if (slash > 0) byPath.TryGetValue(own.Substring(0, slash), out parent); }
                ITxObject existing = null;
                var package = plan.Packages.FirstOrDefault(p => TransferPaths.Same(p.OriginalSource, table.Value(row, "COJT")));
                if (package?.Conflict == TransferConflict.Skip) parent = null;
                if (parent is ITxObjectCollection)
                {
                    var candidates = (SceneAccess.Children(parent)).Cast<ITxObject>().Where(o => o.Name == name
                        && (!type.Contains("Compound") || o.GetType().Name == type)).ToList();
                    if (candidates.Count > 1) errors.Add("目标父容器中同名对象不唯一: " + own);
                    else if (candidates.Count == 1)
                    {
                        existing = candidates[0];
                        if (existing.GetType().Name != type) errors.Add("目标同名对象类型不一致: " + own+"；源端="+type+"；接收端="+existing.GetType().Name
                            +"。请先在接收工程核对或撤销错误实例，再重新检查。");
                        if (package != null && !type.Contains("Compound")
                            &&LibraryPersistence.ReferenceIssue(existing,plan.Destination(package),plan.TargetRoot) is string referenceIssue)
                        {
                            errors.Add("目标同名实例"+referenceIssue+"："+name
                                +"\n实际引用："+LibraryPersistence.StorageDescription(existing)
                                +"\n计划引用："+plan.Destination(package)+"\n此检查核验已有场景实例的存储引用；场景名称与 cojt 文件名不需要一致。"
                                +"\n未覆盖该对象；已有实例需改位置时请在接收端另存为到指定文件夹，或核对并撤销失败实例后重新传输。");
                        }
                    }
                }
                if(package!=null&&existing==null&&!type.Contains("Compound")&&package.Conflict!=TransferConflict.Skip
                    &&LibraryInsertion.NeedsCompatibility(plan.Destination(package))
                    &&LibraryPersistence.FindReusable(plan.Destination(package),type)==null)
                {
                    if(!compatibilityChecked){compatibilityIssue=LibraryInsertion.SupportIssue(plan.Destination(package));compatibilityChecked=true;}
                    if(compatibilityIssue!=null)errors.Add("中文路径兼容不可用："+package.Name+"\n计划引用："+plan.Destination(package)
                        +"\n"+compatibilityIssue+"\n已在预检阶段停止，尚未复制模型或创建场景实例。");
                }
                if (id.Length > 0) bySource[id] = existing;
                byPath[own] = existing;
            }
        }

        internal static TransferJob Execute(TransferPlan plan, Action<string> log)
        {
            bool mayHaveRun = false;
            try
            {
                // 保持数据快照只读打开，直至场景事务结束，防止校验后文件被替换。
                using (var snapshot = new FileStream(plan.DataFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var modelSnapshot=plan.Kind=="weld"&&plan.IncludeWeldParts?new FileStream(plan.WeldModelsFile,FileMode.Open,FileAccess.Read,FileShare.Read):null)
                    return ExecuteLocked(plan, log, () => mayHaveRun = true);
            }
            catch (Exception ex) { throw new TransferExecutionException(ex.Message, mayHaveRun, ex); }
        }

        private static TransferJob ExecuteLocked(TransferPlan plan, Action<string> log, Action started)
        {
            var existing = Jobs.Read(plan.JobId);
            if (existing != null)
            {
                if (existing.Fingerprint != plan.Stamp()) throw new InvalidOperationException("任务标识与计划指纹不一致");
                return existing;
            }
            if (string.IsNullOrEmpty(plan.PreparedStamp) || plan.Stamp() != plan.PreparedStamp)
                throw new InvalidOperationException("计划在预检后发生变化，请重新检查");
            var files = plan.InspectFiles(false);
            if (!files.Ok) throw new InvalidOperationException(string.Join("\n", files.Errors));
            if (plan.TargetSceneStamp != SceneStamp()) throw new InvalidOperationException("目标场景在预检后变化，请重新检查");
            var checks = Preflight(plan);
            if (!(bool)checks["ok"]) throw new InvalidOperationException(checks["errors"].ToString());
            TransferJob record;
            if (!Jobs.Begin(plan, out record)) return record;
            started();
            try
            {
                using(var combinedUndo=plan.Kind=="weld"&&plan.IncludeWeldParts?TxTools.Common.SceneUndoScope.Begin("焊点与绑定零件传输"):null)
                {
                if (plan.Kind == "model") DevicePrototypes.Prepare(plan,log);
                if (plan.Kind == "weld")
                {
                    var map=new Dictionary<string,string>(plan.ObjectMap);
                    string modelMessage="";
                    if(plan.IncludeWeldParts)
                    {
                        var models=plan.WeldModelsPlan();DevicePrototypes.Prepare(models,log);
                        var skipped=new HashSet<string>(models.Packages.Where(p=>p.Conflict==TransferConflict.Skip).Select(p=>Path.GetFullPath(p.OriginalSource)),StringComparer.OrdinalIgnoreCase);
                        var parts=StructureIO.RebuildStructure(models.DataFile,Parent(models.TargetParentId),true,log,
                            0,0,0,null,models.ModelPaths(),true,skipped,models.SourceFrame,models.TargetFrame);
                        record.ObjectMap=parts.ObjectMap;
                        if(parts.Failed>0||parts.Errors.Count>0)throw new InvalidOperationException("绑定零件重建未完成，尚未传输焊点。\n"+parts+"\n"+string.Join("\n",parts.Errors));
                        foreach(var item in parts.ObjectMap)map[item.Key]=item.Value;
                        modelMessage="绑定零件："+parts+"\n";
                    }
                    var report = WeldPointIO.RebuildWeldPoints(plan.DataFile, plan.Project, plan.BindOnly, log, map, plan.WeldBindings,
                        plan.AllowUnboundWelds,plan.SourceFrame,plan.TargetFrame);
                    foreach (var error in report.Errors) log(error);
                    record.Message = modelMessage+report + "\n" + string.Join("\n", report.Errors.Take(20));
                    record.State = report.Failed == 0 && report.Errors.Count == 0 ? "succeeded" : "failed";
                }
                else
                {
                    var skip = new HashSet<string>(plan.Packages.Where(p => p.Conflict == TransferConflict.Skip)
                        .Select(p => Path.GetFullPath(p.OriginalSource)), StringComparer.OrdinalIgnoreCase);
                    var report = StructureIO.RebuildStructure(plan.DataFile, Parent(plan.TargetParentId), true, log,
                        plan.OriginX, plan.OriginY, plan.OriginZ, null, plan.ModelPaths(), true, skip,plan.SourceFrame,plan.TargetFrame);
                    record.ObjectMap = report.ObjectMap;
                    foreach (var error in report.Errors) log(error);
                    record.Message = report + "\n" + string.Join("\n", report.Errors.Take(20));
                    record.State = report.Failed == 0 && report.Errors.Count == 0 ? "succeeded" : "failed";
                    if(record.State=="succeeded")
                    {
                        DevicePrototypes.Prepare(plan,log);
                        record.Message+="\n模型已绑定正式库引用；未保存或重载工程。";
                    }
                }
                TxApplication.RefreshDisplay();
                }
            }
            catch (Exception ex) { record.State = "failed"; record.Message = ex.ToString(); }
            Jobs.Save(record);
            return record;
        }
    }

    internal sealed class TransferExecutionException : Exception
    {
        internal bool MayHaveRun { get; }
        internal TransferExecutionException(string message, bool mayHaveRun, Exception inner) : base(message, inner) { MayHaveRun = mayHaveRun; }
    }

    public sealed class CrossEnvTransferCheckTool : TxAgentToolBase
    {
        public override string Name => "crossenv_transfer_check";
        public override string Description => "只读核验传输计划的目标工程、场景和焊点绑定，不执行复制或重建。";
        public override bool IsReadOnly => true;
        public override JObject InputSchema => JObject.Parse("{'type':'object','properties':{'plan':{'type':'object'}},'required':['plan']}");
        public override string Execute(JObject input)
        {
            try { return TransferRuntime.Preflight(input["plan"].ToObject<TransferPlan>()).ToHostJson(Formatting.None); }
            catch (Exception ex) { return new JObject { ["ok"] = false, ["errors"] = new JArray(ex.Message) }.ToHostJson(Formatting.None); }
        }
    }

    public sealed class CrossEnvTransferExecuteTool : TxAgentToolBase
    {
        public override string Name => "crossenv_transfer_execute";
        public override string Description => "按已预检计划执行一次目标场景重建，持久化任务状态以避免超时后重复重建。";
        public override bool IsReadOnly => false;
        public override JObject InputSchema => JObject.Parse("{'type':'object','properties':{'plan':{'type':'object'}},'required':['plan']}");
        public override string Execute(JObject input)
        {
            try
            {
                var messages = new StringBuilder();
                var job = TransferRuntime.Execute(input["plan"].ToObject<TransferPlan>(), s => messages.AppendLine(s));
                return new JObject { ["ok"] = job.State == "succeeded", ["job"] = JObject.FromObject(job), ["log"] = messages.ToString() }.ToHostJson(Formatting.None);
            }
            catch (TransferExecutionException ex)
            { return new JObject { ["ok"] = false, ["error"] = ex.Message, ["not_started"] = !ex.MayHaveRun }.ToHostJson(Formatting.None); }
            catch (Exception ex) { return new JObject { ["ok"] = false, ["error"] = ex.Message, ["not_started"] = false }.ToHostJson(Formatting.None); }
        }
    }

    public sealed class CrossEnvTransferStatusTool : TxAgentToolBase
    {
        public override string Name => "crossenv_transfer_status";
        public override string Description => "只读查询跨环境任务状态；没有状态时不能据此推断请求未执行。";
        public override bool IsReadOnly => true;
        public override JObject InputSchema => JObject.Parse("{'type':'object','properties':{'job_id':{'type':'string'}},'required':['job_id']}");
        public override string Execute(JObject input)
        {
            try { var record = TransferRuntime.Jobs.Read((string)input["job_id"]); return new JObject
                { ["ok"] = true, ["job"] = record == null ? (JToken)JValue.CreateNull() : JObject.FromObject(record) }.ToHostJson(Formatting.None); }
            catch (Exception ex) { return new JObject { ["ok"] = false, ["error"] = ex.Message }.ToHostJson(Formatting.None); }
        }
    }
}
