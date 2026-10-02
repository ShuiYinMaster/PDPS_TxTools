namespace DLAT.JTReader {
public class FaceEnt {
	/** Face degree */
	public int cDeg;

	/** Empty degrees (opt for emptyFaceSlots()) */
	public int cEmptyDeg;

	/** Idx into _viFaceVtxIndices of cDeg incident vts */
	public int iFVI;

	/** Idx into _viFaceAttrIndices of cAttr attributes */
	public int iFAI;

	/** Number of face attributes */
	public int cFaceAttrs;

	/** User flags */
	public int uFlags;

	/** Degree-ring attr mask as a UInt64 */
	public long uAttrMask;

	/** Degree-ring attr mask as a BitVec */
	public BitVector pvbAttrMask;

	/**
	 * Constructor.
	 */
	public FaceEnt(){
		cDeg = 0;
		uFlags = 0;
		cEmptyDeg = 0;
		cFaceAttrs = 0;
		iFVI = -1;
		iFAI = -1;
		uAttrMask = 0;
	}
}

}