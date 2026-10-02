using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace DLAT.JTReader {
    [XmlRoot(ElementName = "devices")]
    public class TxKinDevices {
        [XmlElement(ElementName = "device")]
        public List<TxKinDevice> device;

        private TxKinDevices() { }

        public static TxKinDevices Create(string xml) {
            var serializer = new XmlSerializer(typeof(TxKinDevices));
            //deserialize string xml
            using (var reader = new StringReader(xml)) {
                return serializer.Deserialize(reader) as TxKinDevices;
            }
        }
    }

    [XmlRoot(ElementName = "device")]
    public class TxKinDevice {
        [XmlElement] public int baseFrameKey;
        [XmlElement] public int refFrameKey;
        [XmlElement] public int toolFrameKey;
        [XmlElement] public int tcpFrameKey;
        [XmlElement] public Pose homePose;

        [XmlArray] [XmlArrayItem("pose")]
        public List<Pose> poses;
        [XmlArray] [XmlArrayItem("joint")]
        public List<Joint> joints;
        
    }
    public class Pose {
        [XmlAttribute] public string key;
        [XmlArray("values")] [XmlArrayItem("value")]
        public List<float> values;
    }

    public class Joint {
        [XmlAttribute] public string key;
        [XmlElement] public int jointType;
        [XmlElement] public int parentLink;
        [XmlElement] public int childLink;
        [XmlElement] public Axis axis1;
        [XmlElement] public Axis axis2;
        [XmlElement] public double userValue;
        [XmlElement] public double maxSpeed;
        [XmlElement] public double maxAcc;
        [XmlElement] public bool related;
        [XmlElement] public bool locked;
        [XmlElement] public string function;
        [XmlElement] public JointRange jointRange;
    }
    
    public class Axis {
        [XmlElement] public double x;
        [XmlElement] public double y;
        [XmlElement] public double z;
    }

    public class JointRange {
        [XmlElement] public int limitType;
        [XmlElement] public double highLimit;
        [XmlElement] public double lowLimit;
    }
    
}