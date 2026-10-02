using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class VertexShaderAttributeData : BaseAttributeData {
        public BaseShaderData shaderData;
        public VertexShaderAttributeData(Element ele) : base(ele) {
            var data = ele.dataStream;
            shaderData = new BaseShaderData(data);
            var version = data.ReadVersionNumber();
        }
    }
}
