namespace DLAT.JTReader {
public class DualVFMeshWrapper {
	/**  */
	private DualVFMesh _dualVFMesh;

	/**
	 * 
	 * @param dualVFMesh
	 */
	public DualVFMeshWrapper(DualVFMesh dualVFMesh){
		_dualVFMesh = dualVFMesh;
	}

	/**
	 * 
	 * @return ---
	 */
	public int numFaces(){
		return _dualVFMesh.numVts();
	}

	/**
	 * 
	 * @return ---
	 */
	public int numVts(){
		return _dualVFMesh.numFaces();
	}

	/**
	 * 
	 * @return ---
	 */
	public int numAttrs(){
		return _dualVFMesh.numAttrs();
	}

	/**
	 * 
	 * @return ---
	 */
	public int getVVBAttrMasksSize(){
		return _dualVFMesh.getVVBAttrMasksSize();
	}

	/**
	 * 
	 * @param  index
	 * @return       ---
	 */
	public FaceEnt getFaceEnt(int index){
		return _dualVFMesh.getFaceEnt(index);
	}

	/**
	 * 
	 * @param  iFace
	 * @return       ---
	 */
	public int faceFlag(int iFace){
		return _dualVFMesh.vtxFlags(iFace);
	}

	/**
	 * 
	 * @param  iFace
	 * @return       ---
	 */
	public int faceGrp(int iFace){
		return _dualVFMesh.vtxGrp(iFace);
	}

	/**
	 * 
	 * @param  iFace
	 * @return       ---
	 */
	public int faceNumVts(int iFace){
		return _dualVFMesh.valence(iFace);
	}

	/**
	 * 
	 * @param  iFace
	 * @param  iVSlot
	 * @return        ---
	 */
	public int faceVtx(int iFace, int iVSlot){
		return _dualVFMesh.face(iFace, iVSlot);
	}

	/**
	 * 
	 * @param  iFace
	 * @param  iVtx
	 * @return       ---
	 */
	public int faceVtxAttr(int iFace, int iVtx){
		return _dualVFMesh.vtxFaceAttr(iFace, iVtx);
	}

	/**
	 * 
	 * @param  iVtx
	 * @return      ---
	 */
	public int vtxFlags(int iVtx){
		return _dualVFMesh.vtxFlags(iVtx);
	}

	/**
	 * 
	 * @param  iVtx
	 * @return      ---
	 */
	public int valence(int iVtx){
		return _dualVFMesh.valence(iVtx);
	}

	/**
	 * 
	 * @param  iVtx
	 * @param  iFaceSlot
	 * @return           ---
	 */
	public int face(int iVtx, int iFaceSlot){
		return _dualVFMesh.face(iVtx, iFaceSlot);
	}

	/**
	 * 
	 * @param  iVtx
	 * @param  iFace
	 * @return       ---
	 */
	public int vtxFaceAttr(int iVtx, int iFace){
		return _dualVFMesh.vtxFaceAttr(iVtx, iFace);
	}
}

}