using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class FragmentShaderAttributeData : BaseAttributeData {
        public BaseShaderData shaderData;
        public FragmentShaderAttributeData(Element ele) : base(ele) {
            var data = ele.dataStream;
            shaderData = new BaseShaderData(data);
            var version = data.ReadVersionNumber();
        }
    }

    public class BaseShaderData {
        public byte version;
        public int shaderLanguage;
        public uint inlineSourceFlag;
        public string sourceCode;
        public ShaderParameter[] shaderParameters;

        public BaseShaderData(StreamReader data) {
            version = data.ReadVersionNumber();
            shaderLanguage = data.ReadI32();
            inlineSourceFlag = data.ReadU32();
            sourceCode = data.ReadMbString();
            var shaderParamCount = data.ReadI32();
            shaderParameters = new ShaderParameter[shaderParamCount];
            for (var i = 0; i < shaderParamCount; i++) {
                shaderParameters[i] = new ShaderParameter(data);
            }
        }
    }
    
    public class ShaderParameter {
        public string paramName;
        public int paramType;
        public uint valueClass;
        public uint direction;
        public uint semanticBinding;
        public uint variability;
        public uint reservedField;
        public uint[] value;

        public ShaderParameter(StreamReader data) {
            paramName = data.ReadMbString();
            paramType = data.ReadI32();
            valueClass = data.ReadU32();
            direction = data.ReadU32();
            semanticBinding = data.ReadU32();
            variability = data.ReadU32();
            reservedField = data.ReadU32();
            value = new uint[16];
            for (int i = 0; i < 16; i++) {
                value[i] = data.ReadU32();
            }
        }
        
    }
}
