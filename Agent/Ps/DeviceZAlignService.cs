using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Tecnomatix.Engineering;
using TxTools.Common;
using TxTools.ExportByColor;

namespace TxTools.Agent.Ps
{
    public static class DeviceZAlignService
    {
        private sealed class Pending
        {
            internal string Id;
            internal TxTransformation Original,Desired;
            internal DeviceZMath.Measurement Before;
            internal JObject Row;
        }

        public static string AlignSelection() { return Execute(new DeviceZOptions { Scope="selected" },true); }

        /// <summary>SDK calls stay on the PS thread; mesh reads and restoration occur outside undo transactions.</summary>
        public static string Execute(DeviceZOptions options,bool align)
        {
            try
            {
                options=options??new DeviceZOptions();
                var document=TxApplication.ActiveDocument;
                if(document==null)return "Error: 没有打开的研究文档。";
                if(SceneUndoScope.HasActiveTransaction)return "Error: 网格测量不能在已有场景事务内重载实体表示。";
                var generation=SceneUndoScope.DocumentGeneration;
                var rows=new JArray();
                var targets=ResolveTargets(document,options,align,rows);
                var pending=new List<Pending>();
                foreach(var id in targets)
                {
                    EnsureDocument(document,generation);
                    var row=new JObject {["object_id"]=id};rows.Add(row);
                    try
                    {
                        var target=document.GetObjectById(id);
                        if(target==null)throw new InvalidOperationException("资源已不存在。");
                        row["name"]=target.Name;
                        string skip=SkipReason(target,options);
                        if(skip!=null){row["status"]="skipped";row["reason"]=skip;continue;}
                        var mesh=WorldTriangleReader.Read(document,id,options.MaximumTriangles,null);
                        target=document.GetObjectById(id); // Reload may invalidate previously captured wrappers.
                        var location=target as ITxLocatableObject;
                        if(location==null)throw new InvalidOperationException("资源没有世界位姿。");
                        var original=new TxTransformation(location.AbsoluteLocation);
                        var measurement=DeviceZMath.Analyze(mesh,options);
                        row["measurement"]=ToJson(measurement,options);
                        DeviceZMath.Correction correction;
                        try
                        {
                            correction=DeviceZMath.Plan(mesh,measurement,options,
                                new DeviceZMath.V3(original[0,0],original[1,0],original[2,0]));
                        }
                        catch(InvalidOperationException ex)
                        {row["status"]="blocked";row["reason"]=ex.Message;continue;}
                        row["planned_rotation_deg"]=correction.AngleDeg;
                        row["planned_z_shift_mm"]=correction.ShiftZ;
                        bool already=Math.Abs(measurement.MinZ-options.TargetZ)<=options.PositionTolerance
                            &&(!options.Level||measurement.TiltDeg<=options.AngleToleranceDeg);
                        if(already){row["status"]="already_aligned";continue;}
                        row["status"]="needs_alignment";
                        if(!align)continue;
                        if(!target.CanBeModified||!location.CanLocate())throw new InvalidOperationException("资源当前不可修改或不可定位。");
                        pending.Add(new Pending {Id=id,Original=original,Desired=Apply(original,correction),Before=measurement,Row=row});
                    }
                    catch(Exception ex){if(ex is AppearanceGeometrySource.RestoreException)throw;row["status"]="error";row["reason"]=ex.Message;}
                }
                if(align&&pending.Count>0)
                {
                    EnsureDocument(document,generation);
                    // No display or representation reload is performed inside this transaction.
                    using(var undo=SceneUndoScope.Begin(document,"Agent 设备网格调平与落地"))
                        foreach(var item in pending)
                        {
                            undo.EnsureDocument();
                            try
                            {
                                var target=document.GetObjectById(item.Id);
                                var location=target as ITxLocatableObject;
                                if(location==null||!target.CanBeModified||!location.CanLocate())throw new InvalidOperationException("写入前资源已不可定位。");
                                if(!SamePose(location.AbsoluteLocation,item.Original,1e-7))throw new InvalidOperationException("测量后资源位姿发生变化，请重新扫描。");
                                string skip=SkipReason(target,options);
                                if(skip!=null)throw new InvalidOperationException(skip);
                                location.AbsoluteLocation=item.Desired;
                                item.Row["status"]="written_unverified";
                            }
                            catch(Exception ex){item.Row["status"]="error";item.Row["reason"]=ex.Message;}
                        }
                    TxApplication.RefreshDisplay();
                    foreach(var item in pending.Where(p=>(string)p.Row["status"]=="written_unverified"))
                    {
                        EnsureDocument(document,generation);
                        try
                        {
                            var mesh=WorldTriangleReader.Read(document,item.Id,options.MaximumTriangles,null);
                            var after=DeviceZMath.Analyze(mesh,options);
                            var current=(document.GetObjectById(item.Id) as ITxLocatableObject)?.AbsoluteLocation;
                            bool verified=current!=null&&SamePose(current,item.Desired,1e-6)
                                &&Math.Abs(after.MinZ-options.TargetZ)<=options.PositionTolerance
                                &&(!options.Level||(after.Reliable&&after.TiltDeg<=options.AngleToleranceDeg));
                            item.Row["after"]=ToJson(after,options);
                            item.Row["status"]=verified?"aligned":"verification_failed";
                            if(!verified)item.Row["reason"]="已写入但复查未通过；场景变更保留，可在当前工程 Ctrl+Z 撤销。";
                        }
                        catch(Exception ex){if(ex is AppearanceGeometrySource.RestoreException)throw;item.Row["status"]="verification_failed";item.Row["reason"]="已写入，复查失败："+ex.Message+"；可 Ctrl+Z 撤销。";}
                    }
                }
                return new JObject
                {
                    ["mode"]=align?"align":"scan",["geometry_source"]="double_precision_world_triangles",
                    ["target_z_mm"]=options.TargetZ,["level"]=options.Level,
                    ["position_tolerance_mm"]=options.PositionTolerance,["angle_tolerance_deg"]=options.AngleToleranceDeg,
                    ["summary"]=new JObject {
                        ["total"]=rows.Count,["aligned"]=Count(rows,"aligned"),["already_aligned"]=Count(rows,"already_aligned"),
                        ["needs_alignment"]=Count(rows,"needs_alignment"),["blocked"]=Count(rows,"blocked"),
                        ["skipped"]=Count(rows,"skipped"),["errors"]=Count(rows,"error"),["verification_failed"]=Count(rows,"verification_failed")},
                    ["objects"]=rows,["undo"]=align?"已完成的位姿修改处于同一撤销分组；部分失败不自动回滚。":""
                }.ToString();
            }
            catch(Exception ex){return "Error: 设备 Z 操作失败："+ex.Message+"。已完成的位姿修改不会自动回滚。";}
        }
        private static int Count(JArray rows,string status){return rows.Count(r=>(string)r["status"]==status);}
        private static void EnsureDocument(TxDocument document,long generation)
        {if(generation!=SceneUndoScope.DocumentGeneration||!Equals(document,TxApplication.ActiveDocument))throw new InvalidOperationException("工程已切换，已停止设备操作。");}

        private static List<string> ResolveTargets(TxDocument document,DeviceZOptions options,bool align,JArray rows)
        {
            var candidates=new List<ITxObject>();
            if(options.ObjectIds!=null&&options.ObjectIds.Length>0)
            {
                foreach(string id in options.ObjectIds.Distinct())
                {
                    var item=document.GetObjectById(id);
                    if(item==null)rows.Add(new JObject {["object_id"]=id,["status"]="error",["reason"]="ID 不存在于当前工程。"});
                    else candidates.Add(item);
                }
            }
            else if(options.Scope=="selected")
            {
                foreach(ITxObject item in TxApplication.ActiveSelection.GetItems())candidates.Add(item);
                if(candidates.Count==0)throw new InvalidOperationException("请先选择资源，或提供 object_ids。");
            }
            else
            {
                if(align)throw new InvalidOperationException("批量校正需明确 object_ids 或当前选择，不能直接校正全场景。");
                var all=document.PhysicalRoot.GetAllDescendants(new TxTypeFilter(typeof(ITxObject)));
                foreach(ITxObject item in all)if(IsResource(item))candidates.Add(item);
            }
            var eligible=candidates.Where(o=>IsResource(o)&&SkipReason(o,options)==null).ToList();
            var selectedIds=new HashSet<string>(eligible.Select(o=>o.Id));
            var result=new List<string>();var seen=new HashSet<string>();
            foreach(var item in candidates)
            {
                if(!seen.Add(item.Id))continue;
                bool nested=false;var parent=item.Collection as ITxObject;var chain=new HashSet<string>();
                while(parent!=null&&chain.Add(parent.Id))
                {if(selectedIds.Contains(parent.Id)){nested=true;break;}parent=parent.Collection as ITxObject;}
                if(nested)
                {rows.Add(new JObject {["object_id"]=item.Id,["name"]=item.Name,["status"]="skipped",["reason"]="已由选中的上层资源覆盖，避免重复移动子对象。"});continue;}
                result.Add(item.Id);
            }
            return result;
        }
        private static bool IsResource(ITxObject target)
        {
            return target!=null&&!(target is ITxOperation)&&!(target is ITxGeometry)
                &&(target is ITxComponent||AppearanceGeometrySource.IsAppearance(target)||target is TxCompoundResource);
        }
        private static string SkipReason(ITxObject target,DeviceZOptions options)
        {
            if(!IsResource(target))return "目标必须是整台资源、组件或零件外观；不能直接移动几何、坐标系或操作点。";
            foreach(var keyword in options.IgnoreKeywords??new string[0])
                if(!string.IsNullOrWhiteSpace(keyword)&&(target.Name??"").IndexOf(keyword,StringComparison.OrdinalIgnoreCase)>=0)
                    return "名称命中忽略关键字："+keyword;
            var current=target;var seen=new HashSet<string>();
            while(current!=null&&seen.Add(current.Id))
            {
                if(SpecialType(current))return "目标或装配祖先属于机器人、枪、夹爪、工具或输送线。";
                current=current.Collection as ITxObject;
            }
            var location=target as ITxLocatableObject;
            if(location==null)return "目标没有可写的世界位姿。";
            var attachment=location.AttachmentParent;
            if(attachment is ITxComponent||attachment is ITxDevice)return "目标附着于其他设备或组件，应先确认其安装关系。";
            var collection=target as ITxObjectCollection;
            if(collection!=null)
            {
                var robots=collection.GetAllDescendants(new TxTypeFilter(typeof(TxRobot)));
                if(robots!=null&&robots.Count>0)return "资源包含机器人，已阻止整体调平。";
            }
            return null;
        }
        private static bool SpecialType(ITxObject value)
        {
            for(Type t=value.GetType();t!=null;t=t.BaseType)
                if(new[]{"TxRobot","TxGun","TxWeldGun","TxGripper","TxTool","TxHuman","TxHumanModel","TxConveyor","TxBasePlcConveyor"}.Contains(t.Name))return true;
            return false;
        }
        private static bool SamePose(TxTransformation a,TxTransformation b,double tolerance)
        {
            for(int r=0;r<3;r++)for(int c=0;c<4;c++)if(!DeviceZMath.IsFinite(a[r,c])||Math.Abs(a[r,c]-b[r,c])>tolerance)return false;
            return true;
        }
        private static TxTransformation Apply(TxTransformation original,DeviceZMath.Correction correction)
        {
            // Left-multiply the existing 3x3, preserving scale and mirror as well as internal geometry offsets.
            var result=new TxTransformation(original);
            for(int c=0;c<3;c++)
            {
                var column=DeviceZMath.Rotate(correction.Rotation,new DeviceZMath.V3(original[0,c],original[1,c],original[2,c]));
                result[0,c]=column.X;result[1,c]=column.Y;result[2,c]=column.Z;
            }
            var t=original.Translation;
            var moved=correction.Transform(new DeviceZMath.V3(t.X,t.Y,t.Z));
            result.Translation=new TxVector(moved.X,moved.Y,moved.Z);
            return result;
        }
        private static JObject ToJson(DeviceZMath.Measurement m,DeviceZOptions options)
        {
            return new JObject {
                ["minimum_z_mm"]=m.MinZ,["maximum_z_mm"]=m.MaxZ,["triangle_count"]=m.TriangleCount,
                ["lowest_point_world_mm"]=new JArray(m.LowestPoint.X,m.LowestPoint.Y,m.LowestPoint.Z),
                ["lowest_geometry_id"]=m.LowestGeometryId,
                ["support_status"]=m.SupportStatus,["support_reason"]=m.Reason,
                ["tilt_deg"]=m.SupportTriangles>0?(JToken)new JValue(m.TiltDeg):JValue.CreateNull(),
                ["support_normal_world"]=m.SupportTriangles>0?(JToken)new JArray(m.Normal.X,m.Normal.Y,m.Normal.Z):JValue.CreateNull(),
                ["height_difference_world_x_mm"]=m.Reliable?(JToken)new JValue(-m.Normal.X/m.Normal.Z*m.SpanX):JValue.CreateNull(),
                ["height_difference_world_y_mm"]=m.Reliable?(JToken)new JValue(-m.Normal.Y/m.Normal.Z*m.SpanY):JValue.CreateNull(),
                ["support_normal_spread_deg"]=m.NormalSpreadDeg,["support_area_mm2"]=m.SupportArea,
                ["support_plane_max_error_mm"]=m.ResidualMax,["support_plane_rms_error_mm"]=m.ResidualRms,
                ["support_height_range_mm"]=m.SupportHeightRange,["support_triangle_count"]=m.SupportTriangles,
                ["support_geometry_ids"]=new JArray(m.SupportGeometryIds)
            };
        }
    }
}
