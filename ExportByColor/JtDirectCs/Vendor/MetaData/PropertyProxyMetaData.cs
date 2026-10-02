using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class PropertyProxyMetaData {
        public Dictionary<string, (byte, object)> property;
        public PropertyProxyMetaData(Element ele) {
            var data = ele.dataStream;
            short version = 0;
            if (ele.majorVersion > 8)
                version = ele.majorVersion == 9 ? data.ReadI16() : data.ReadU8();
            property = new Dictionary<string, (byte, object)>();
            for (; ; ) {
                var keyLen = data.ReadI32();
                if (keyLen == 0)
                    break;
                var key = Encoding.Unicode.GetString(data.ReadBytes(keyLen * 2));
                var type = data.ReadU8();
                //var value = type switch {
                //    1 => (object)Encoding.Unicode.GetString(data.ReadBytes(data.ReadI32() * 2)),
                //    2 => (object)data.ReadI32(),
                //    3 => (object)data.ReadF32(),
                //    4 => (object)new DatePropertyValue(data),
                //    _ => throw new NotImplementedException()
                //};
                Func<object> valueFunc = () => {
                    if (type == 1) return data.ReadMbString();
                    else if (type == 2) return data.ReadI32();
                    else if (type == 3) return data.ReadF32();
                    else if (type == 4) return new DatePropertyValue(data);
                    else throw new NotImplementedException();
                };
                var val = valueFunc();
                //Debug.Log("Type:#g" + type + "#w Key:" + key);
                //Debug.Log(" Value:" + val, 1);
                property.Add(key, (type, val));

                if (key == "TxKinModelingBuffer")
                    ele.segment.file.txKinModelingBuffer = val.ToString();
            }
        }

    }
    public class DatePropertyValue {
        public short year, month, day, hour, minute, second;
        public DatePropertyValue(StreamReader data) {
            year = data.ReadI16();
            month = data.ReadI16();
            day = data.ReadI16();
            hour = data.ReadI16();
            minute = data.ReadI16();
            second = data.ReadI16();
        }
        public override string ToString() {
            return new DateTime(year, month, day, hour, minute, second).ToString();
        }
    }
}
