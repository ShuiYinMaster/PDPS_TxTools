namespace DLAT.JTReader {
    public class BaseNodeData {
        public Element header;
        public uint nodeFlags;
        public int[] attributeObjectID;
        public BaseNodeData(Element ele) {
            header = ele;
            var data = ele.dataStream;
            switch(ele.majorVersion) {
                case 8:
                    header.objectID = data.ReadI32();
                    break;
                case 9:
                    var version9 = data.ReadI16();
                    break;
                case 10:
                    var version10 = data.ReadU8();
                    break;
            }

            nodeFlags = data.ReadU32();
            var attributeCount = data.ReadI32();
            attributeObjectID = new int[attributeCount];
            for (int i = 0; i < attributeCount; ++i)
                attributeObjectID[i] = data.ReadI32();
        }
    }
}
