using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class Tri_StripSetShapeLODData {
        public static GUID typeID = new GUID("10DD10AB-2AC8-11D1-9B-6B-00-80-C7-BB-59-97");
        public VertexShapeLODData vertexShapeLodData;
        public short versionNumber;
        public VertexBasedShapeCompressedRepData vertexBasedShapeData;
        
        public Tri_StripSetShapeLODData(Element ele) {
            var data = ele.dataStream;
            vertexShapeLodData = new VertexShapeLODData(ele);
            versionNumber = data.ReadVersionNumber();
            if (ele.majorVersion < 9) {
                vertexBasedShapeData = new VertexBasedShapeCompressedRepData(data);
            }



            // Console.Write(GetVertices().Count + "    ");
            // Console.Write(GetNormals().Count + "    ");
            // var indices = GetIndices();
            // Console.Write(indices.Count + "    ");
            // foreach (var i in indices) {
            //     Console.Write(i.Count + "  ");
            // }
            // Console.WriteLine();
        }
        
        public List<float> GetVertices() {
            if(vertexBasedShapeData != null)
                return vertexBasedShapeData.GetVertices();
            return vertexShapeLodData.GetVertices();
        }
        
        public List<float> GetNormals() {
            if(vertexBasedShapeData != null)
                return vertexBasedShapeData.GetNormals();
            return vertexShapeLodData.GetNormals();
        }
        
        public List<List<int>> GetIndices() {
            if(vertexBasedShapeData != null)
                return vertexBasedShapeData.GetIndices();
            return vertexShapeLodData.GetIndices();
        }
    }
}
