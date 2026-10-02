using System;
using System.Collections.Generic;
using System.IO;

namespace DLAT.JTReader {
    public class TopoMeshCompressedRepDataV2 {
        public TopoMeshCompressedRepDataV1 topoMeshCompressedRepDataV1;

        public TopoMeshCompressedRepDataV2(Element ele) {
            var data = ele.dataStream;
            topoMeshCompressedRepDataV1 = new TopoMeshCompressedRepDataV1(ele);

            int versionNumber = data.ReadI16();
            if (versionNumber != 1) {
                throw new Exception("Found invalid version number: " + versionNumber);
            }

            ulong vertexBindings = data.ReadU64();
            if ((vertexBindings & 0x8000000000000000ul) == 0)
                return;


            uint numberOfAuxiliaryFields = data.ReadU32();

            GUID[] uniqueFieldIdentifiers = new GUID[numberOfAuxiliaryFields];
            int[] fieldTypes = new int[numberOfAuxiliaryFields];
            List<List<int>> dataExponentsLists = new List<List<int>>();
            List<List<int>> dataUpperMantissaeLists = new List<List<int>>();
            List<List<int>> dataLowerMantissaeLists = new List<List<int>>();
            List<List<int>> dataU320Lists = new List<List<int>>();
            List<List<int>> dataU321Lists = new List<List<int>>();
            List<List<int>> dataU322Lists = new List<List<int>>();
            int[] auxiliaryDataHashs = new int[numberOfAuxiliaryFields];

            for (int i = 0; i < numberOfAuxiliaryFields; i++) {
                uniqueFieldIdentifiers[i] = new GUID(data);
                fieldTypes[i] = data.ReadU8();
                int fieldTypeComponents = CODEC.fieldTypeData[fieldTypes[i]].Item2;

                for (int j = 0; j < fieldTypeComponents; j++) {
                    string fieldTypeData = CODEC.fieldTypeData[fieldTypes[i]].Item1;
                    if (fieldTypeData[0] == 'F') {
                        dataExponentsLists.Add(Int32CDP.ReadVecU32(data, PredictorType.PredNull));

                        if (fieldTypeData.Equals("F64")) {
                            dataUpperMantissaeLists.Add(Int32CDP.ReadVecU32(data, PredictorType.PredNull));
                        }

                        if (fieldTypeData.Equals("F32") || fieldTypeData.Equals("F64")) {
                            dataLowerMantissaeLists.Add(Int32CDP.ReadVecU32(data, PredictorType.PredNull));
                        }
                    }
                    else {
                        dataU320Lists.Add(Int32CDP.ReadVecU32(data, PredictorType.PredNull));

                        if (fieldTypeData.Equals("U32") || fieldTypeData.Equals("I32") ||
                            fieldTypeData.Equals("U64") || fieldTypeData.Equals("I64")) {
                            dataU321Lists.Add(Int32CDP.ReadVecU32(data, PredictorType.PredNull));
                        }

                        if (fieldTypeData.Equals("U64") || fieldTypeData.Equals("I64")) {
                            dataU322Lists.Add(Int32CDP.ReadVecU32(data, PredictorType.PredNull));
                        }
                    }
                }

                auxiliaryDataHashs[i] = data.ReadI32();
            }
        }
    }
}