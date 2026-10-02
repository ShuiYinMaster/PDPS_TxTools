using System;
using System.Collections.Generic;
using System.IO;

namespace DLAT.JTReader {
    public class TopologicallyCompressedRepData {
        public List<int[]> faceDegress;
        public List<int> vertexValences;
        public List<int> vertexGroups;
        public List<int> vertexFlags;
        public List<int[]> faceAttributeMasks30LSBs;
        public List<int> faceAttributeMasks8_30nextMSBs;
        public List<int> faceAttributeMask8_4MSBs;
        public long[] highDegreeFaceAttributeMasks;
        public List<int> splitFaceSyms;
        public List<int> splitFacePositions;
        public int compositeHash;
        public TopologicallyCompressedVertexRecords vertexRecords;

        public TopologicallyCompressedRepData(Element ele) {
            var data = ele.dataStream;

            faceDegress = new List<int[]>();
            for (int i = 0; i < 8; ++i) 
                faceDegress.Add(Int32CDP.ReadVecI32(data, PredictorType.PredNull).ToArray());
            vertexValences = Int32CDP.ReadVecI32(data, PredictorType.PredNull);
            vertexGroups   = Int32CDP.ReadVecI32(data, PredictorType.PredNull);
            vertexFlags    = Int32CDP.ReadVecI32(data, PredictorType.PredLag1);
            
            faceAttributeMasks30LSBs = new List<int[]>();
            for(int i = 0; i < 8; i++) {
                faceAttributeMasks30LSBs.Add(Int32CDP.ReadVecI32(data, PredictorType.PredNull).ToArray());
            }

            faceAttributeMasks8_30nextMSBs = Int32CDP.ReadVecI32(data, PredictorType.PredNull);
            if(ele.majorVersion == 9)
                faceAttributeMask8_4MSBs = Int32CDP.ReadVecI32(data, PredictorType.PredNull);
            var _highDegreeFaceAttributeMasks = data.ReadVecU32();
            highDegreeFaceAttributeMasks = new long[_highDegreeFaceAttributeMasks.Count];
            for (int i = 0; i < _highDegreeFaceAttributeMasks.Count; ++i)
                highDegreeFaceAttributeMasks[i] = _highDegreeFaceAttributeMasks[i];
            splitFaceSyms      = Int32CDP.ReadVecI32(data, PredictorType.PredLag1);
            splitFacePositions = Int32CDP.ReadVecI32(data, PredictorType.PredNull);

            long readHash = data.ReadU32();
            
            vertexRecords = new TopologicallyCompressedVertexRecords(data);
        }

        public List<List<int>> GetIndices() {
            var meshCoderDriver = new MeshCoderDriver();
            meshCoderDriver.setInputData(vertexValences, faceDegress,
                vertexGroups, vertexFlags,
                faceAttributeMasks30LSBs, faceAttributeMasks8_30nextMSBs, faceAttributeMask8_4MSBs,
                highDegreeFaceAttributeMasks, splitFaceSyms, splitFacePositions);
            return meshCoderDriver.decode();
        }
        
    }
}