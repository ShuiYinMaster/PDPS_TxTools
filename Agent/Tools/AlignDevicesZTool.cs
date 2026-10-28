using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using TxTools.Agent.Core;
using TxTools.Agent.Ps;

namespace TxTools.Agent.Tools
{
    public sealed class AlignDevicesZTool : TxAgentToolBase
    {
        public override string Name { get { return "align_devices_z"; } }
        public override string Description { get { return "使用真实世界坐标三角网格对选中资源或 object_ids 调平并落地。默认识别分散的底部共面支撑，保持水平朝向，整体旋转后使网格最低点到 target_z_mm。支撑不明、不共面、存在更低几何或倾斜超范围时停止该对象；level=false 可明确选择只沿 Z 落地。扫描与校正使用相同容差。临时实体加载/显示会恢复，校正后重新读取网格复查；位姿变更需要审批，可 Ctrl+Z 撤销，部分失败不自动回滚。"; } }
        public override bool IsReadOnly { get { return false; } }
        public override JObject InputSchema { get { return DeviceZToolArguments.Schema(true); } }
        public override string Execute(JObject input)
        {
            try { return PsBridge.AlignDevicesZ(DeviceZToolArguments.Parse(input,true)); }
            catch(ArgumentException ex){return "Error: "+ex.Message;}
        }
    }

    internal static class DeviceZToolArguments
    {
        internal static JObject Schema(bool align)
        {
            var schema=JObject.Parse(@"{
                'type':'object','properties':{
                    'object_ids':{'type':'array','items':{'type':'string'},'minItems':1,'description':'整台资源/组件/外观的场景 ID；优先于 scope，父子重叠只处理上层'},
                    'scope':{'type':'string','enum':['scene','selected'],'description':'扫描可使用 scene 或 selected；校正需要 selected 或明确 object_ids'},
                    'keywords':{'type':'array','items':{'type':'string'},'description':'要忽略的对象名称关键字'},
                    'target_z_mm':{'type':'number','default':0},
                    'position_tolerance_mm':{'type':'number','exclusiveMinimum':0,'maximum':10,'default':0.1},
                    'angle_tolerance_deg':{'type':'number','exclusiveMinimum':0,'maximum':1,'default':0.05},
                    'plane_tolerance_mm':{'type':'number','exclusiveMinimum':0,'maximum':10,'default':0.5},
                    'max_tilt_deg':{'type':'number','exclusiveMinimum':0,'maximum':10,'default':3,'description':'资源已接近摆正时允许的最大倾角'},
                    'support_geometry_ids':{'type':'array','items':{'type':'string'},'description':'可选支撑几何 ID，来自扫描结果的 support_geometry_ids；仅限制支撑识别，最低点仍检查完整网格'},
                    'max_triangles':{'type':'integer','minimum':100,'maximum':2000000,'default':1000000},
                    'level':{'type':'boolean','default':true,'description':'true=支撑面调平后落地；false=只按网格最低点沿 Z 平移'}
                }}");
            schema["properties"]["scope"]["default"]=align?"selected":"scene";
            return schema;
        }
        internal static DeviceZOptions Parse(JObject input,bool align)
        {
            input=input??new JObject();
            string scope=ToolInputHelpers.String(input["scope"],align?"selected":"scene");
            if(scope!="scene"&&scope!="selected")throw new ArgumentException("scope 必须是 scene 或 selected。");
            var options=new DeviceZOptions {
                Scope=scope,ObjectIds=Strings(input["object_ids"],"object_ids"),IgnoreKeywords=Strings(input["keywords"],"keywords"),
                SupportGeometryIds=Strings(input["support_geometry_ids"],"support_geometry_ids"),
                TargetZ=ToolInputHelpers.Number(input["target_z_mm"],0,"target_z_mm",double.MinValue),
                PositionTolerance=Positive(input["position_tolerance_mm"],0.1,"position_tolerance_mm",10),
                AngleToleranceDeg=Positive(input["angle_tolerance_deg"],0.05,"angle_tolerance_deg",1),
                PlaneTolerance=Positive(input["plane_tolerance_mm"],0.5,"plane_tolerance_mm",10),
                MaximumTiltDeg=Positive(input["max_tilt_deg"],3,"max_tilt_deg",10)
            };
            if(input["level"]!=null&&input["level"].Type!=JTokenType.Boolean)throw new ArgumentException("level 必须是布尔值。");
            options.Level=ToolInputHelpers.Bool(input["level"],true);
            double maximum=ToolInputHelpers.Number(input["max_triangles"],1000000,"max_triangles",100);
            if(maximum>2000000||maximum!=Math.Truncate(maximum))throw new ArgumentException("max_triangles 必须是 100..2000000 的整数。");
            options.MaximumTriangles=(int)maximum;
            if(options.AngleToleranceDeg>options.MaximumTiltDeg)throw new ArgumentException("angle_tolerance_deg 不能大于 max_tilt_deg。");
            if(align&&scope=="scene"&&options.ObjectIds.Length==0)throw new ArgumentException("校正需要当前选择或明确 object_ids。");
            return options;
        }
        private static string[] Strings(JToken token,string name)
        {
            if(token==null||token.Type==JTokenType.Null)return new string[0];
            var array=token as JArray;if(array==null)throw new ArgumentException(name+" 必须是字符串数组。");
            var result=new List<string>();
            foreach(var item in array)
            {
                if(item.Type!=JTokenType.String||string.IsNullOrWhiteSpace((string)item))throw new ArgumentException(name+" 不允许空值或非字符串项。");
                result.Add(((string)item).Trim());
            }
            if(name=="object_ids"&&result.Count==0)throw new ArgumentException("object_ids 不能为空数组；省略该参数以使用 scope。");
            return result.ToArray();
        }
        private static double Positive(JToken token,double fallback,string name,double maximum)
        {
            double value=ToolInputHelpers.Number(token,fallback,name);
            if(value<=0||value>maximum)throw new ArgumentException(name+" 必须大于 0 且不超过 "+maximum+"。");
            return value;
        }
    }
}
