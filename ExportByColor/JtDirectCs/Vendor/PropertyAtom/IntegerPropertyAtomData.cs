using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class IntegerPropertyAtomData : BasePropertyAtomData {
        public int value;
        public IntegerPropertyAtomData(Element ele) : base(ele) {
            var data = ele.dataStream;
            var version = 0;
            if (ele.majorVersion > 8)
                version = ele.majorVersion > 9 ? data.ReadU8() : data.ReadI16();
            value = data.ReadI32();
        }
        
        public override string ToString() {
            return value.ToString();
        }
        
        //重载int 类型
        public static implicit operator int(IntegerPropertyAtomData data) {
            return data.value;
        }
        
    }
}
