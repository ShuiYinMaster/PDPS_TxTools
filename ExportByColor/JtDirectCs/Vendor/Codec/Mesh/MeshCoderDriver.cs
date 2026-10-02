using System;
using System.Collections.Generic;

namespace DLAT.JTReader {
public class MeshCoderDriver {
 public DualVFMeshWrapper DecodedMesh => new DualVFMeshWrapper(_pMeshDecoder.vfm());
	/**  */
	private List<int> _vviOutValSyms;

	/**  */
	private List<int[]> _viOutDegSyms;

	/**  */
	private List<int> _viOutFGrpSyms;

	/**  */
	private List<int> _vuOutFaceFlags;

	/**  */
	private List<int[]> _vvuOutAttrMasks;

	/**  */
	private List<int> _faceAttributeMask8_30;

	/**  */
	private List<int> _faceAttributeMask8_4;

	/**  */
	private long[] _vuOutAttrMasksLrg;

	/**  */
	private List<int> _viOutSplitVtxSyms;

	/**  */
	private List<int> _viOutSplitPosSyms;

	/**  */
	private int[] _iValReadPos;

	/**  */
	private int _iDegReadPos;

	/**  */
	private int _iVGrpReadPos;

	/**  */
	private int _iFFlagReadPos;

	/**  */
	private int[] _iAttrMaskReadPos;

	/**  */
	private int _iAttrMaskLrgReadPos;

	/**  */
	private int _iSplitFaceReadPos;

	/**  */
	private int _iSplitPosReadPos;

	/**  */
	private MeshDecoder _pMeshDecoder;

	/**
	 * Constructor.
	 */
	public MeshCoderDriver(){
	}

	/**
	 * 
	 * @param vviOutValSyms
	 * @param viOutDegSyms
	 * @param viOutFGrpSyms
	 * @param vuOutFaceFlags
	 * @param vvuOutAttrMasks
	 * @param faceAttributeMask8_30
	 * @param faceAttributeMask8_4
	 * @param vuOutAttrMasksLrg
	 * @param viOutSplitVtxSyms
	 * @param viOutSplitPosSyms
	 */
	public void setInputData(List<int> vviOutValSyms, List<int[]> viOutDegSyms,
			List<int> viOutFGrpSyms, List<int> vuOutFaceFlags, List<int[]> vvuOutAttrMasks,
			List<int> faceAttributeMask8_30, List<int> faceAttributeMask8_4,
			long[] vuOutAttrMasksLrg, List<int> viOutSplitVtxSyms, List<int> viOutSplitPosSyms){
		_vviOutValSyms = vviOutValSyms;
		_viOutDegSyms = viOutDegSyms;
		_viOutFGrpSyms = viOutFGrpSyms;
		_vuOutFaceFlags = vuOutFaceFlags;
		_vvuOutAttrMasks = vvuOutAttrMasks;
		_faceAttributeMask8_30 = faceAttributeMask8_30;
		_faceAttributeMask8_4 = faceAttributeMask8_4;
		_vuOutAttrMasksLrg = vuOutAttrMasksLrg;
		_viOutSplitVtxSyms = viOutSplitVtxSyms;
		_viOutSplitPosSyms = viOutSplitPosSyms;
	}

	/**
	 * Decodes the mesh.
	 * @return List of lists: vertex and normal indices
	 */
	public List<List<int>> decode(){
		// Allocate a coder
		if(_pMeshDecoder == null){
			_pMeshDecoder = new MeshDecoder(this);
		}

		// Reset the symbol counters
		_iValReadPos = new int[8];
		_iAttrMaskReadPos = new int[8];
		_iDegReadPos = 0;
		_iVGrpReadPos = 0;
		_iFFlagReadPos = 0;
		_iAttrMaskLrgReadPos = 0;
		_iSplitFaceReadPos = 0;
		_iSplitPosReadPos = 0;

		// Run the decoder
		_pMeshDecoder.run();

		// Assert that ALL symbols have been consumed
		for(int i = 0; i < 8; i++){
			if(	(_iValReadPos[i] != _viOutDegSyms[i].Length) ||
				(_iAttrMaskReadPos[i] != _vvuOutAttrMasks[i].Length)){
				throw new ArgumentException("ERROR: Not all symbols have been consumed!");
			}
		}

		if(	(_iDegReadPos != _vviOutValSyms.Count) ||
			(_iVGrpReadPos != _viOutFGrpSyms.Count) ||
			(_iFFlagReadPos != _vuOutFaceFlags.Count) ||
			(_iAttrMaskLrgReadPos != _vuOutAttrMasksLrg.Length) ||
			(_iSplitFaceReadPos != _viOutSplitVtxSyms.Count) ||
			(_iSplitPosReadPos != _viOutSplitPosSyms.Count)){
			throw new ArgumentException("ERROR: Not all symbols have been consumed!");
		}

		// Set output VFMesh (wrapper)
		DualVFMeshWrapper dualVFMeshWrapper = new DualVFMeshWrapper(_pMeshDecoder.vfm());

		List<int> vertexIndices = new List<int>();
		List<int> normalIndices = new List<int>();

		int numFaces = dualVFMeshWrapper.numFaces();
		for(int iFace = 0; iFace < numFaces; iFace++){
			// Show only visible faces
			//if(dualVFMeshWrapper.vtxFlags(iFace) == 0){
			//if(dualVFMeshWrapper.faceFlag(iFace) == 0){
			if(dualVFMeshWrapper.faceGrp(iFace) >= 0){
				for(int iVSlot = 0; iVSlot < dualVFMeshWrapper.valence(iFace); iVSlot++){
					int vertexIndex = dualVFMeshWrapper.face(iFace, iVSlot);
					vertexIndices.Add(vertexIndex);

					int iAttr = dualVFMeshWrapper.vtxFaceAttr(iFace, vertexIndex);
					normalIndices.Add(iAttr);
				}
			}
		}

		List<List<int>> indexLists = new List<List<int>>();
		indexLists.Add(vertexIndices);
		indexLists.Add(normalIndices);
		return indexLists;
	}

	/**
	 * 
	 * @param  iCCntx
	 * @return        Next degree symbol
	 */
	public int _nextDegSymbol(int iCCntx){
		int eSym = -1;
		if(_iValReadPos[iCCntx] < _viOutDegSyms[iCCntx].Length){
			eSym = _viOutDegSyms[iCCntx][_iValReadPos[iCCntx]++];
		}
		return eSym;
	}

	/**
	 * 
	 * @return Next value symbol
	 */
	public int _nextValSymbol(){
		int eSym = -1;
		if(_iDegReadPos < _vviOutValSyms.Count){
			eSym = _vviOutValSyms[_iDegReadPos++];
		}
		return eSym;
	}

	/**
	 * 
	 * @return Next face group symbol
	 */
	public int _nextFGrpSymbol(){
		int eSym = -1;
		if(_iVGrpReadPos < _viOutFGrpSyms.Count){
			eSym = _viOutFGrpSyms[_iVGrpReadPos++];
		}
		return eSym;
	}

	/**
	 * 
	 * @return Next vertex flag symbol
	 */
	public int _nextVtxFlagSymbol(){
		int eSym = 0;
		if(_iFFlagReadPos < _vuOutFaceFlags.Count){
			eSym = _vuOutFaceFlags[_iFFlagReadPos++];
		}
		return eSym;
	}

	/**
	 * 
	 * @param  iCCntx
	 * @return        Next attr mask symbol
	 */
	public long _nextAttrMaskSymbol(int iCCntx){
		long eSym = 0;
		int readpos = _iAttrMaskReadPos[iCCntx];
		if (readpos < _vvuOutAttrMasks[iCCntx].Length){
			eSym = (uint)_vvuOutAttrMasks[iCCntx][readpos];
		}
		if(iCCntx == 7) {
			if (_faceAttributeMask8_4 != null)
				eSym |= ((((long)_faceAttributeMask8_4[readpos]) << 30) +
				         (((long)_faceAttributeMask8_30[readpos]) << 30)
					);
			else
				eSym |= (((long)(uint)_faceAttributeMask8_30[readpos]) << 30);

		}
		_iAttrMaskReadPos[iCCntx]++;
		return eSym;
	}

	/**
	 * 
	 * @param iopvbAttrMask
	 * @param cDegree
	 */
	public void _nextAttrMaskSymbol(BitVector iopvbAttrMask, int cDegree){
		if(_iAttrMaskLrgReadPos < _vuOutAttrMasksLrg.Length){
			iopvbAttrMask.setLength(cDegree);
			int nWords = (cDegree + BitVector.cWordBits - 1) >> BitVector.cBitsLog2;

			for(int i = 0; i < nWords; i++){
				iopvbAttrMask[i] = _vuOutAttrMasksLrg[_iAttrMaskLrgReadPos + i];
			}

			_iAttrMaskLrgReadPos += nWords;
		} else {
			iopvbAttrMask.setLength(0);
		}
	}

	/**
	 * 
	 * @return Split face symbol
	 */
	public int _nextSplitFaceSymbol(){
		int eSym = -1;
		if(_iSplitFaceReadPos < _viOutSplitVtxSyms.Count){
			eSym = _viOutSplitVtxSyms[_iSplitFaceReadPos++];
		}
		return eSym;
	}

	/**
	 * 
	 * @return Split position symbol
	 */
	public int _nextSplitPosSymbol(){
		int eSym = -1;
		if(_iSplitPosReadPos < _viOutSplitPosSyms.Count){
			eSym = _viOutSplitPosSyms[_iSplitPosReadPos++];
		}
		return eSym;
	}

	/**
	 * Computes a "compression context" from 0 to 7 inclusive for faces on vertex iVtx. The context
	 * is based on the vertex's valence, and the total _known_ degree of already-coded faces on the
	 * vertex at the time of the call.
	 * @param  iVtx
	 * @param  pVFM
	 * @return      Compression context
	 */
	public int _faceCntxt(int iVtx, DualVFMesh pVFM){
		// Here, we are going to gather data to be used to determine a
		// compression contest for the face degree.
		int cVal = pVFM.valence(iVtx);
		int nKnownFaces = 0;
		int cKnownTotDeg = 0;
		for(int i = 0; i < cVal; i++){
			int iTmpFace = pVFM.face(iVtx, i);
			if(!pVFM.isValidFace(iTmpFace)){
				continue;
			}
			nKnownFaces++;
			cKnownTotDeg += pVFM.degree(iTmpFace);
		}

		int iCCntxt = 0;
		if(cVal == 3){
			// Regular tristrip-like meshes tend to have degree 6 faces
			iCCntxt = (cKnownTotDeg < nKnownFaces * 6) ? 0 : (cKnownTotDeg == nKnownFaces * 6) ? 1 : 2;
		} else if(cVal == 4){
			// Regular quadstrip-like meshes tend to have degree 4 faces
			iCCntxt = (cKnownTotDeg < nKnownFaces * 4) ? 3 : (cKnownTotDeg == nKnownFaces * 4) ? 4 : 5;
		} else if(cVal == 5){
			// Pentagons are all lumped into context 6
			iCCntxt = 6;
		} else {
			// All other polygons are lumped into context 7
			iCCntxt = 7;
		}

		return iCCntxt;
	}
}

}
