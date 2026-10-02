using System;
using System.Collections.Generic;

namespace DLAT.JTReader {
    
public class MeshDecoder {
	/**  */
	private DualVFMesh _pDstVFM;

	/**  */
	private MeshCoderDriver _pTMC;

	/** Stack of incomplete 'active faces' */
	private List<int> _viActiveFaces; 

	/**  */
	private BitVector _vbRemovedActiveFaces;

	/**  */
	private int _iFaceAttrCtr;

	/**
	 * Constructor.
	 * @param meshCoderDriver
	 */
	public MeshDecoder(MeshCoderDriver meshCoderDriver){
		_pTMC = meshCoderDriver;
		_viActiveFaces = new List<int>();
		_vbRemovedActiveFaces = new BitVector();
	}

	/**
	 * 
	 * @param  val
	 * @param  n
	 * @return    ---
	 */
	private int IncModN(int val, int n){
		return (val == (n - 1)) ? 0 : (val + 1);
	}

	/**
	 * 
	 * @param  val
	 * @param  n
	 * @return     ---
	 */
	private int DecModN(int val, int n){
		return (val - 1) & ((val == 0) ? 0 : -1) | (n - 1) & ((val != 0) ? 0 : -1);
	}

	/**
	 * Runs the mesh encoder/decoder machine. If decoding is being performed, it consumes the mesh
	 * coding symbols from pre-filled member variables to produce the output VFMesh _pDstVFM.
	 */
	public void run(){
		// Assert state is consistent and ready to co/dec
		if(_pDstVFM == null){
			_pDstVFM = new DualVFMesh();
		}

		if(_pDstVFM == null){
			throw new ArgumentException("ERROR: DualVertexFacesMesh couldn't be initiated!");
		}

		_pDstVFM.clear();
		clear();

		// Co/dec connected mesh components one at a time
		bool bFoundComponent = true;
		while(bFoundComponent){
			bFoundComponent = runComponent(bFoundComponent);
		}
	}

	/**
	 * 
	 * @return ---
	 */
	public DualVFMesh vfm(){
		return _pDstVFM;
	}

	/**
	 * 
	 */
	private void clear(){
		_viActiveFaces.Clear();
		_vbRemovedActiveFaces.setLength(0);
		_iFaceAttrCtr = 0;
	}

	/**
	 * Decodes one "connected component" (contiguous group of polygons) into _pDstVFM. Because the
	 * polygonal model may be formed of multiple disconnected mesh components, it may be necessary
	 * for run() to call this method multiple times. This method returns obFoundComponent = True
	 * if it actually encoded a new mesh component, and obFoundComponent = False if it did not.
	 * @param  obFoundComponent
	 * @return                  ---
	 */
	private bool runComponent(bool obFoundComponent){
		int iFace;
		obFoundComponent = initNewComponent(obFoundComponent);
		if(!obFoundComponent){
			return obFoundComponent;
		}

		while((iFace = nextActiveFace()) != -1){
			completeV(iFace);
			removeActiveFace(iFace);
		}

		return obFoundComponent;
	}

	/**
	 * Locates an unencoded vertex and begins the encoding process for the newly-found mesh component.
	 * @param  obFoundComponent
	 * @return                  ---
	 */
	private bool initNewComponent(bool obFoundComponent){
		obFoundComponent = true;

		// Call ioVtxInit() to start us off with the seed face
		// from a new "connected component" of polygons.
		int iVtx, i;
		if((iVtx = ioVtxInit()) == -1){
			obFoundComponent = false; // All vtxs are processed
			return obFoundComponent;
		}

		int cVal = _pDstVFM.valence(iVtx);
		for(i = 0; i < cVal; i++){
			activateF(iVtx, i); // Process all faces
		}

		return obFoundComponent;
	}

	/**
	 * Begins decoding a new connected mesh component by calling ioVtx() to read the next vertex
	 * from the symbol stream.
	 * @return ---
	 */
	private int ioVtxInit(){
		return ioVtx(-1, -1);
	}

	/**
	 * Read a vertex valence symbol, vertex group number, and vertex flags from the input symbols
	 * stream. Create a new vertex on _pDstVFM with this data, and return the new vertex number.
	 * It is this method's responsibility to detect the end of the input symbol stream by returning
	 * -1 when that happens.
	 * @param  iFace
	 * @param  iVSlot
	 * @return        ---
	 */
	private int ioVtx (int iFace , int iVSlot){
		// Obtain a VERTEX VALENCE symbol
		int eSym = _pTMC._nextValSymbol();
		int iVtxVal;
		int iVtx = -1;

		if(eSym > -1){
			// Create a new vtxt on the VFMesh
			iVtx = _pDstVFM.numVts();
			iVtxVal = eSym;
			_pDstVFM.newVtx(iVtx, iVtxVal, 0);
			_pDstVFM.setVtxGrp(iVtx, _pTMC._nextFGrpSymbol());
			_pDstVFM.setVtxFlags(iVtx, _pTMC._nextVtxFlagSymbol());
		}

		return iVtx;
	}

	/**
	 * Read a face degree symbol, and attribute mask bit vector, create a new DualVFMesh face,
	 * initialize the face attribute record numbers from a running counter, and return the new
	 * face number. If the degree symbol read from the input symbol stream is 0, signify this
	 * by returning -1.
	 * @param  iVtx
	 * @param  jFSlot
	 * @return        ---
	 */
	private int ioFace(int iVtx, int jFSlot){
		// Obtain a FACE DEGREE symbol
		int iCntxt = _pTMC._faceCntxt(iVtx, _pDstVFM);
		int eSym = _pTMC._nextDegSymbol(iCntxt);
		int cDeg, iFace = -1;
		if(eSym != 0){
			// Create a new face on the VFMesh
			iFace = _pDstVFM.numFaces();
			cDeg = eSym;
			int nFaceAttrs = 0;
			if(cDeg <= DualVFMesh.cMBits){
				long uAttrMask = _pTMC._nextAttrMaskSymbol(Math.Min(7, Math.Max(0, (cDeg - 2))));
				for (ulong uMask = unchecked((ulong)uAttrMask); uMask != 0; ){
					nFaceAttrs += (int)(uMask & 1);
					uMask >>= 1;
				}
				_pDstVFM.newFace(iFace, cDeg, nFaceAttrs, uAttrMask, 0);
			} else {
				BitVector vbAttrMask = _pDstVFM.newAttrMaskBitVector();
				_pTMC._nextAttrMaskSymbol(vbAttrMask, cDeg);
				for(int i = 0; i < cDeg; i++){
					if(vbAttrMask.test(i)){
						nFaceAttrs++;
					}
				}
				_pDstVFM.newFace(iFace, cDeg, nFaceAttrs, vbAttrMask, 0);
			}

			// Error check for a corrupt degree or attrmask
			if(nFaceAttrs > cDeg){
				if(nFaceAttrs > cDeg){
					throw new ArgumentException("ERROR: Something's invalid!");
				}
				return -2;
			}

			// Set up the face attributes
			for(int iAttrSlot = 0; iAttrSlot < nFaceAttrs; iAttrSlot++){
				_pDstVFM.setFaceAttr(iFace, iAttrSlot, _iFaceAttrCtr++);
			}
		}
		return iFace;
	}

	/**
	 * Consumes a split offset symbol from the SPLIT offset symbol stream, and determines the face
	 * number referenced by the offset. Returns the referenced face number.
	 * @param  iVtx
	 * @param  jFSlot
	 * @return        ---
	 */
	private int ioSplitFace(int iVtx, int jFSlot){
		// Obtain a SPLITFACE symbol
		int eSym = _pTMC._nextSplitFaceSymbol();
		if(eSym < -1){
			throw new ArgumentException("ERROR: Something's invalid!");
		}

		int iOffset = -1, iFace = -1;
		if(eSym > -1){
			// Use the offset to index into the active face queue
			// to determine the actual face number.
			iOffset = eSym;
			int cLen = _viActiveFaces.Count;
			if((iOffset <= 0) || (iOffset > cLen)){
				throw new ArgumentException("ERROR: Something's invalid!");
			}

			iFace = _viActiveFaces[cLen - iOffset];
		}

		return iFace;
	}

	/**
	 * Consumes a split position symbol from the associated symbol stream, and returns the vertex
	 * slot number on the current split face at which the topological split/merge occurred.
	 * @param  iVtx
	 * @param  jFSlot
	 * @return        ---
	 */
	private int ioSplitPos(int iVtx, int jFSlot){
		// Obtain a SPLITVTX symbol
		int eSym = _pTMC._nextSplitPosSymbol();
		if(eSym < -1){
			throw new ArgumentException("ERROR: Something's invalid!");
		}

		int iVSlot = -1;
		if(eSym > -1){
			// Return the vtx slot number
			iVSlot = eSym;
		}

		return iVSlot;
	}

	/**
	 * Completes the VFMesh face iFace on _pDstVFM by calling activateV() and completeV() for
	 * each as-yet inactive incident vertexes in the face's degree ring.
	 * @param iFace
	 */
	private void completeV(int iFace){
		// While there is an empty vtx slot on the face
		int jVtxSlot, iVtx;
		int iVSlot = 0;

		while((jVtxSlot = _pDstVFM.findVtxSlot(iFace, -1)) != -1){
			// Create and return a vtx iVtx, attaching it to iFace at vtx
			// slot jVtxSlot.
			iVtx = activateV(iFace, jVtxSlot);

			// Assert FV consistency
			if((_pDstVFM.vtx(iFace, jVtxSlot) != iVtx) || (_pDstVFM.face(iVtx, iVSlot) != iFace)){
				throw new ArgumentException("ERROR: Found invalid VF");
			}

			// Process the faces of iVtx starting from face slot
			// jVtxSlot where iVtx is incident on iFace.
			completeF(iVtx, jVtxSlot);

			// Invariant "VF": vtx(iVtx).face(iVSlot) == iFace &&
			// face(iFace).vtx(jVtxSlot) == iVtx
		}
	}

	/**
	 * "Activates" the VFMesh face, on _pDstVFM, at face iFace vertex slot iVSlot by calling ioFace()
	 * to obtain a new vertex number and hooking it up to the topological structure. If the face is a
	 * SPLIT face, then call ioSplitFace() and ioSplitPos() to get the information necessary to connect
	 * to an already-active face. Note that we use the term "activate" here to mean "read" for mesh
	 * decoding.
	 * @param  iVtx
	 * @param  iVSlot
	 * @return        ---
	 */
	private int activateF(int iVtx, int iVSlot){
		int jFSlot;

		// ioFace might return -2 as an error condition
		int iFace = ioFace(iVtx, iVSlot);
		if(iFace >= 0){							// If a new active face
			if(	!_pDstVFM.setVtxFace(iVtx, iVSlot, iFace) ||
				!_pDstVFM.setFaceVtx(iFace, 0, iVtx) ||
				!addActiveFace(iFace)){
				return -2;
			}
		} else if(iFace == -1){					// Face already exists, so Split
			iFace = ioSplitFace(iVtx, iVSlot);	// v's index in ActiveSet, returns v
			jFSlot = ioSplitPos(iVtx, iVSlot); // Position of iVtx in v
			_pDstVFM.setVtxFace(iVtx, iVSlot, iFace);
			addVtxToFace(iVtx, iVSlot, iFace, jFSlot);
		}

		return iFace;
	}

	/**
	 * "Activates" the VFMesh vertex, on _pDstVFM, at face iFace vertex slot iVSlot by calling ioFace()
	 * to obtain a new face number and hooking it up to the topological structure. Note that we use
	 * the term "activate" here to mean "read" for mesh decoding.
	 * @param  iFace
	 * @param  iVSlot
	 * @return        ---
	 */
	private int activateV(int iFace, int iVSlot){
		int iVtx = ioVtx(iFace, iVSlot);	// I/O valence; create a vtx
		_pDstVFM.setVtxFace(iVtx, 0, iFace);
		addVtxToFace(iVtx, 0, iFace, iVSlot);
		return iVtx;
	}

	/**
	 * Completes the vertex iVtx on _pDstVFM by activating all inactive faces incident upon it. As an
	 * optimization, the user must also pass in iVSlot which is the vertex slot on face 0 of iVtx
	 * where iVtx is located. This method begins its examination of iVtx's faces at face 0 by working
	 * its way around the vertex in both CCW and CW directions, checking to see if there are any faces
	 * that can be hooked into iVtx without calling activateF(). This can happen when a face is completed
	 * by a nearby vertex before coming here. The situation can be detected by traversing the topology of
	 * the _pDstVFM over to the neighboring vertex and checking if it already has a face number for the
	 * corresponding face entry on iVtx. If so, then iVtx and the already completed face are connected
	 * together, and the next face around iVtx is examined. When the process can go no further, this
	 * method calls _activateF() on the remaining unresolved span of faces around the vertex.
	 * @param iVtx
	 * @param iVSlot
	 */
	private void completeF(int iVtx, int iVSlot){
		int i, vp, vn, jp, jn, iVtx2;
		int cVal = _pDstVFM.valence(iVtx);

		// Walk CCW from face slot 0, attempting to link in as many
		// already-reachable faces as possible until we reach one
		// that is inactive.
		vp = _pDstVFM.face(iVtx, 0);
		jp = iVSlot;
		i = 1;

		while((vn = _pDstVFM.face(iVtx, i)) != -1){	// Forces "FV" in the "next" direction
			jp = DecModN(jp, _pDstVFM.degree(vp));
			iVtx2 = _pDstVFM.vtx(vp, jp);
			if(iVtx2 == -1){
				break;
			}

			jn = _pDstVFM.findVtxSlot(vn, iVtx2);
			if(jn <= -1){
				throw new ArgumentException("ERROR: Found illegal slot index: " + jn);
			}
			jn = DecModN(jn, _pDstVFM.degree(vn));
			addVtxToFace(iVtx, i, vn, jn);
			vp = vn;
			jp = jn;
			i++;
			if(i >= cVal){
				return;
			}
		}

		// Walk CW from face slot 0, attempting to link in as many
		// already-reachable faces as possible until we reach one
		// that is inactive.
		int ilast = i;
		vp = _pDstVFM.face(iVtx, 0);
		jp = iVSlot;
		i = _pDstVFM.valence(iVtx) - 1;

		while((vn = _pDstVFM.face(iVtx, i)) != -1){	// Forces "VF" in "prev" direction
			jp = IncModN(jp, _pDstVFM.degree(vp));
			iVtx2 = _pDstVFM.vtx(vp, jp);
			if(iVtx2 == -1){
				break;
			}

			jn = _pDstVFM.findVtxSlot(vn, iVtx2);
			if(jn <= -1){
				throw new ArgumentException("ERROR: Found illegal slot index: " + jn);
			}

			jn = IncModN(jn, _pDstVFM.degree(vn));
			addVtxToFace(iVtx, i, vn, jn);
			vp = vn;
			jp = jn;
			i--;

			if(i < ilast){
				return;
			}
		}

		// Activate the remaining faces on iVtx that cannot be decuced from
		// the already-assembled topology in the destination VFMesh.
		for(; ilast <= i; ilast++){
			int iFace = activateF(iVtx, ilast);
			if(iFace < -1){
				throw new ArgumentException("ERROR: Found illegal face index: " + iFace);
			}
		}
	}

	/**
	 * This method connects vertex iVtx into the topology of _pDstVFM at and around iFace. First,
	 * it connects iVtx to iFace's degree ring at position iVSlot. Next, it will connect iVtx into
	 * the faces at the other ends of the shared edges between iVtx and the next vertices CS and
	 * CCW about iFace if necessary.
	 * @param iVtx
	 * @param jFSlot
	 * @param iFace
	 * @param iVSlot
	 */
	private void addVtxToFace(int iVtx, int jFSlot, int iFace, int iVSlot){
		int jFSlotCW = iVSlot;
		int jFSlotCCW = iVSlot;
		int fp, ip, fn, inVal;
		jFSlotCCW = IncModN(jFSlotCCW, _pDstVFM.degree(iFace));
		jFSlotCW = DecModN(jFSlotCW, _pDstVFM.degree(iFace));

		// Connect iVtx to iFace/iVSlot
		_pDstVFM.setFaceVtx(iFace, iVSlot, iVtx);

		// Connect iVtx across the shared edge between iVtx and the vtx CW
		// from iVtx at iFace. Connect iVtx into the face at the other
		// end of this edge if it is not already connected there.
		if((fp = _pDstVFM.vtx(iFace, jFSlotCW)) != -1){
			ip = _pDstVFM.findFaceSlot(fp, iFace);
			int iVSlotCCW = jFSlot;
			iVSlotCCW = IncModN(iVSlotCCW, _pDstVFM.valence (iVtx));
		
			if(_pDstVFM.face(iVtx, iVSlotCCW) == -1){
				ip = DecModN(ip, _pDstVFM.valence(fp));
				_pDstVFM.setVtxFace(iVtx, iVSlotCCW, _pDstVFM.face(fp, ip));
			}
		}

		// Connect iVtx across the shared edge between iVtx and the vtx CCW
		// from iVtx at iFace. Connect iVtx into the face at the other
		// end of this edge if it is not already connected there.
		if((fn = _pDstVFM.vtx(iFace, jFSlotCCW)) != -1){
			inVal = _pDstVFM.findFaceSlot(fn, iFace);
			int iVSlotCW = jFSlot;
			iVSlotCW = DecModN(iVSlotCW, _pDstVFM.valence (iVtx));
		
			if(_pDstVFM.face(iVtx, iVSlotCW) == -1){
				inVal = IncModN(inVal, _pDstVFM.valence(fn));
				_pDstVFM.setVtxFace(iVtx, iVSlotCW, _pDstVFM.face(fn, inVal));
			}
		}
	}

	/**
	 * 
	 * @param  iFace
	 * @return       ---
	 */
	private bool addActiveFace(int iFace){
		_viActiveFaces.Add(iFace);
		return true;
	}

	/**
	 * Returns a face from the active queue to be completed. This needn't be the one at the end of the
	 * queue, because the choice of the next active face can affect how many SPLIT symbols are produced.
	 * This method employs a fairly simple scheme of searching the most recent 16 active faces for the
	 * first one with the smallest number of incomplete slots in its degree ring.
	 * @return ---
	 */
	private int nextActiveFace(){
		int iFace = -1;

		// Search the 16 face record at the end of the
		// queue for the one with lowest remaining degree.
		while((	_viActiveFaces.Count > 0) && _vbRemovedActiveFaces.test(_viActiveFaces[_viActiveFaces.Count - 1])){
			_viActiveFaces.RemoveAt(_viActiveFaces.Count - 1);
		}

		int cLowestEmptyDegree = 9999999;
		int i, iFace0, cEmptyDeg;
		int cWidth = 16;
		for(i = _viActiveFaces.Count - 1; i >= Math.Max(0, _viActiveFaces.Count - cWidth); i--){
			iFace0 = _viActiveFaces[i];
			if(_vbRemovedActiveFaces.test(iFace0)){
				_viActiveFaces.RemoveAt(i); // TOXIC: O(N^2)
				continue;
			}
			cEmptyDeg = _pDstVFM.emptyFaceSlots(iFace0);

			if(cEmptyDeg < cLowestEmptyDegree){
				cLowestEmptyDegree = cEmptyDeg;
				iFace = iFace0;
			}
		}
		// Return the selected active face
		return iFace;
	}

	/**
	 * Removes iFace from the active face queue.
	 * @param iFace
	 */
	private void removeActiveFace(int iFace){
		_vbRemovedActiveFaces.set(iFace);
	}
}

}
