using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class FloatingPropertyAtomData {
        public float value;
        public FloatingPropertyAtomData(Element ele) {
            var data = ele.dataStream;
            var version = 0;
            if (ele.majorVersion > 8)
                version = ele.majorVersion > 9 ? data.ReadU8() : data.ReadI16();
            value = data.ReadF32();
        }
        
        public override string ToString() {
            return value.ToString();
        }
        
        //重载float 类型
        public static implicit operator float(FloatingPropertyAtomData data) {
            return data.value;
        }
    }
}
