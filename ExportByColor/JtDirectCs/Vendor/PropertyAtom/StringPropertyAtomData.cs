using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class StringPropertyAtomData : BasePropertyAtomData {
        public string value;
        public StringPropertyAtomData(Element ele) : base(ele) {
            var data = ele.dataStream;
            var version = 0;
            if (ele.majorVersion > 8)
                version = ele.majorVersion > 9 ? data.ReadU8() : data.ReadI16();
            value = data.ReadMbString();
        }
        
        public override string ToString() {
            return value;
        }
    }
}
