using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class TextureImageAttributeData : BaseAttributeData {
        public TextureData textureData;
        public TextureImageAttributeData(Element ele) : base(ele) {
            var data = ele.dataStream;
            var version = ele.majorVersion > 9 ? data.ReadU8() : data.ReadI16();
            for(int i = 1;i<version;++i) {
                textureData = new TextureData(ele, i);
            }
            if(ele.majorVersion == 10)
                base.ReadBaseAttributeDataFields2(data);
        }

    }
}
