using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Serialization;
using Tecnomatix.Engineering;

namespace TxTools.AutoRecorder
{
    public sealed class VectorData
    {
        public double X,Y,Z;
        public static VectorData From(TxVector v){return v==null?null:new VectorData {X=v.X,Y=v.Y,Z=v.Z};}
        public TxVector ToVector()
        {if(new[]{X,Y,Z}.Any(v=>double.IsNaN(v)||double.IsInfinity(v)||Math.Abs(v)>1e12))throw new InvalidDataException("坐标超出允许范围");return new TxVector(X,Y,Z);}
    }
    public sealed class CameraData
    {
        public VectorData Position,Target,Up;
        public static CameraData From(TxCamera c){return c==null?null:new CameraData {Position=VectorData.From(c.Position),Target=VectorData.From(c.ReferencePoint),Up=VectorData.From(c.UpVector)};}
        public TxCamera ToCamera()
        {if(Position==null||Target==null||Up==null)throw new InvalidDataException("视角数据不完整");return new TxCamera(Target.ToVector(),Position.ToVector(),Up.ToVector());}
    }
    public sealed class ShotData
    {
        public string Name,FocusLabel,FromImage,ToImage;
        public ShotKind Kind;
        public ShotTrigger Trigger;
        public bool Enabled,Smooth,Follow,OrbitInitialized;
        public double Start,Duration,Angle,Radius,Height,StartAngle,EndDistance;
        public int LocationIndex,FocusSource,Axis;
        public VectorData Focus;
        public CameraData From,To;
        public static ShotData FromShot(CameraShot s)
        {return new ShotData {Name=s.Name,Kind=s.Kind,Trigger=s.Trigger,Enabled=s.Enabled,Smooth=s.Smooth,Follow=s.Follow,OrbitInitialized=s.OrbitInitialized,Start=s.Start,Duration=s.Duration,Angle=s.Angle,Radius=s.Radius,Height=s.Height,StartAngle=s.StartAngle,EndDistance=s.EndDistance,LocationIndex=s.LocationIndex,FocusSource=s.FocusSource,Axis=s.Axis,FocusLabel=s.FocusLabel,Focus=VectorData.From(s.Focus),From=CameraData.From(s.From),To=CameraData.From(s.To),FromImage=Encode(s.FromThumbnail),ToImage=Encode(s.ToThumbnail)};}
        public CameraShot ToShot()
        {
            if(Axis<0||Axis>2||FocusSource<0||FocusSource>2||Duration<=0||Duration>100000||Start<0||Start>100000||LocationIndex<1||LocationIndex>100000||Math.Abs(Angle)>36000||Radius<0||Radius>10000000||Math.Abs(Height)>10000000||Math.Abs(StartAngle)>36000||EndDistance<=0||EndDistance>10000000)
                throw new InvalidDataException("镜头参数超出允许范围");
            foreach(double v in new[]{Start,Duration,Angle,Radius,Height,StartAngle,EndDistance})if(double.IsNaN(v)||double.IsInfinity(v))throw new InvalidDataException("镜头包含无效数值");
            return new CameraShot {Name=Name??"镜头",Kind=Kind,Trigger=Trigger,Enabled=Enabled,Smooth=Smooth,Follow=Follow,OrbitInitialized=OrbitInitialized,Start=Start,Duration=Duration,Angle=Angle,Radius=Radius,Height=Height,StartAngle=StartAngle,EndDistance=EndDistance,LocationIndex=LocationIndex,FocusSource=FocusSource,Axis=Axis,FocusLabel=FocusLabel,Focus=Focus?.ToVector(),From=From?.ToCamera(),To=To?.ToCamera(),FromThumbnail=Decode(FromImage),ToThumbnail=Decode(ToImage)};
        }
        private static string Encode(Image image)
        {if(image==null)return null;using(var stream=new MemoryStream()){image.Save(stream,ImageFormat.Png);return Convert.ToBase64String(stream.ToArray());}}
        private static Image Decode(string data)
        {if(string.IsNullOrEmpty(data))return null;if(data.Length>1500000)throw new InvalidDataException("缩略图过大");using(var stream=new MemoryStream(Convert.FromBase64String(data)))using(var image=Image.FromStream(stream)){if(image.Width>1024||image.Height>1024)throw new InvalidDataException("缩略图尺寸过大");return new Bitmap(image);}}
    }
    public sealed class LocationViewData
    {public int Index;public CameraData Camera;}
    public sealed class ShotPreset
    {
        public int Version=1;
        public bool Quick;
        public CameraData Initial;
        public List<ShotData> Shots=new List<ShotData>();
        public List<LocationViewData> Locations=new List<LocationViewData>();
        public static void Save(string path,OperationViewSchedule schedule)
        {
            var preset=new ShotPreset {Quick=schedule.Quick,Initial=CameraData.From(schedule.InitialCamera),Shots=schedule.Shots.Select(ShotData.FromShot).ToList(),Locations=schedule.Keyframes.Where(k=>k!=null&&k.Camera!=null).Select(k=>new LocationViewData {Index=k.LocationIndex,Camera=CameraData.From(k.Camera)}).ToList()};
            string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                using(var writer=XmlWriter.Create(temporary,new XmlWriterSettings {Indent=true}))new XmlSerializer(typeof(ShotPreset)).Serialize(writer,preset);
                if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);
            }
            finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
        public static OperationViewSchedule Load(string path)
        {
            ShotPreset preset;
            using(var reader=XmlReader.Create(path,new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=16000000}))
                preset=(ShotPreset)new XmlSerializer(typeof(ShotPreset)).Deserialize(reader);
            if(preset.Version!=1||preset.Shots==null||preset.Shots.Count>200||preset.Locations==null||preset.Locations.Count>10000)throw new InvalidDataException("不支持的镜头配置文件");
            var schedule=new OperationViewSchedule {Quick=preset.Quick,InitialCamera=preset.Initial?.ToCamera()};
            try
            {
                foreach(var shot in preset.Shots)schedule.Shots.Add(shot.ToShot());
                foreach(var item in preset.Locations){if(item.Index<1||item.Camera==null)throw new InvalidDataException("无效的 Location 视角");schedule.Keyframes.Add(new CameraKeyframe {LocationIndex=item.Index,Camera=item.Camera.ToCamera()});}
                return schedule;
            }
            catch{foreach(var shot in schedule.Shots)shot.DisposeThumbnails();throw;}
        }
    }
}
