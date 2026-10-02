namespace DLAT.JTReader {
    public class VtxEnt {
        /** Vertex valence */
        public int cVal;

        /** Idx into _viVtxFaceIndices of cVal incident faces */
        public int iVFI;

        /** User flags */
        public int uFlags;

        /** Vertex group */
        public int iVGrp;

        /**
         * Constructor.
         */
        public VtxEnt(){
            cVal = 0;
            iVFI = -1;
            uFlags = 0;
            iVGrp = -1;
        }
    }
}