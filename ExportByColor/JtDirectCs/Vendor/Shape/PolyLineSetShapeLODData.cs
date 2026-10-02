namespace DLAT.JTReader {
    public class PolyLineSetShapeLODData : VertexShapeLODData{
        VertexBasedShapeCompressedRepData vertexBasedShapeData;
        public PolyLineSetShapeLODData(Element ele) : base(ele) {
            var data = ele.dataStream;
            var version = data.ReadVersionNumber();
            if(ele.majorVersion == 8)
                vertexBasedShapeData = new VertexBasedShapeCompressedRepData(data);
        }
    }
}