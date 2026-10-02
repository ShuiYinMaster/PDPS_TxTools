using System.Collections.Generic;
using System.Linq;

namespace DLAT.JTReader {
    public class DualVFMesh {
        /**  */
        public static int cMBits = 64;

        /**
         * Subscripted by atom number, the entry contains the vtx valence and points to the location in
         * _viVtxFaceIndices of valence consecutive ints that in turn contain the indices of the incident
         * faces in _vFaceRecs to the vtx.
         */
        private List<VtxEnt> _vVtxEnts;

        /**
         * Subscripted by unique vertex record number, the entry contains the face degree and points to the
         * location in _viFaceVtxIndices of cDeg consecutive ints that in turn contain the indices of the
         * vertices indicent upon the face, in CCW order, in _vVtxRecs.
         */
        private List<FaceEnt> _vFaceEnts;

        /** Combined storage for all vtxs */
        private List<int> _viVtxFaceIndices;

        /** Combined storage for all faces */
        private List<int> _viFaceVtxIndices;

        /** Combined storage for all face attribute record identifiers */
        private List<int> _viFaceAttrIndices;

        /**  */
        private List<BitVector> _vvbAttrMasks;

        /**
         * Constructor.
         */
        public DualVFMesh() {
            _vVtxEnts = new List<VtxEnt>();
            _vFaceEnts = new List<FaceEnt>();
            _viVtxFaceIndices = new List<int>();
            _viFaceVtxIndices = new List<int>();
            _viFaceAttrIndices = new List<int>();
            _vvbAttrMasks = new List<BitVector>();
        }

        /**
         *
         */
        public void clear() {
            _vVtxEnts.Clear();
            _vFaceEnts.Clear();
            _viVtxFaceIndices.Clear();
            _viFaceVtxIndices.Clear();
            _viFaceAttrIndices.Clear();
        }

        /**
         *
         * @return BitVector
         */
        public BitVector newAttrMaskBitVector() {
            BitVector bitVector = new BitVector();
            _vvbAttrMasks.Add(bitVector);
            return bitVector;
        }

        /**
         *
         * @param  iVtx
         * @return      Valence
         */
        public int valence(int iVtx) {
            return _vVtxEnts[iVtx].cVal;
        }

        /**
         *
         * @param  iFace
         * @return       Degree
         */
        public int degree(int iFace) {
            return _vFaceEnts[iFace].cDeg;
        }

        /**
         *
         * @param  iVtx
         * @param  iFaceSlot
         * @return           Face
         */
        public int face(int iVtx, int iFaceSlot) {
            return _viVtxFaceIndices[_vVtxEnts[iVtx].iVFI + iFaceSlot];
        }

        /**
         *
         * @param  iFace
         * @param  iVtxSlot
         * @return          Vertex
         */
        public int vtx(int iFace, int iVtxSlot) {
            return _viFaceVtxIndices[_vFaceEnts[iFace].iFVI + iVtxSlot];
        }

        /**
         *
         * @return Number of vertices
         */
        public int numVts() {
            return _vVtxEnts.Count;
        }

        /**
         *
         * @return Number of faces
         */
        public int numFaces() {
            return _vFaceEnts.Count;
        }

        /**
         *
         * @return Number of attributes
         */
        public int numAttrs() {
            return _viFaceAttrIndices.Count;
        }

        /**
         *
         * @param  iFace
         * @return       Empty face slots
         */
        public int emptyFaceSlots(int iFace) {
            return _vFaceEnts[iFace].cEmptyDeg;
        }

        /**
         *
         * @param  iVtx
         * @param  iValence
         * @param  uFlags
         * @return          New vertex
         */
        public bool newVtx(int iVtx, int iValence, int uFlags) {
            VtxEnt rFE = new VtxEnt();
            _vVtxEnts.Add(rFE);
            if (rFE.cVal != iValence) {
                rFE.cVal = iValence;
                rFE.uFlags = uFlags;
                rFE.iVFI = _viVtxFaceIndices.Count;

                while ((rFE.iVFI + iValence) > _viVtxFaceIndices.Count) {
                    _viVtxFaceIndices.Add(0);
                }

                for (int i = rFE.iVFI; i < (rFE.iVFI + iValence); i++) {
                    _viVtxFaceIndices[i] = -1;
                }
            }

            return true;
        }

        /**
         *
         * @param iVtx
         * @param iVGrp
         */
        public void setVtxGrp(int iVtx, int iVGrp) {
            VtxEnt rFE = _vVtxEnts[iVtx];
            rFE.iVGrp = iVGrp;
        }

        /**
         *
         * @param iVtx
         * @param uFlags
         */
        public void setVtxFlags(int iVtx, int uFlags) {
            VtxEnt rFE = _vVtxEnts[iVtx];
            rFE.uFlags = uFlags;
        }

        /**
         *
         * @param  iVtx
         * @return      ---
         */
        public int vtxGrp(int iVtx) {
            int u = -1;
            if ((iVtx >= 0) && (iVtx < _vVtxEnts.Count)) {
                VtxEnt rFE = _vVtxEnts[iVtx];
                u = rFE.iVGrp;
            }

            return u;
        }

        /**
         *
         * @param  iFace
         * @return       Is the face valid
         */
        public bool isValidFace(int iFace) {
            bool bRet = false;
            if (iFace >= 0 && iFace < _vFaceEnts.Count) {
                FaceEnt rVE = _vFaceEnts[iFace];
                bRet = (rVE.cDeg != 0);
            }

            return bRet;
        }

        /**
         *
         * @param iFace
         * @param cDegree
         * @param cFaceAttrs
         * @param uFaceAttrMask
         * @param uFlags
         */
        public void newFace(int iFace, int cDegree, int cFaceAttrs, long uFaceAttrMask, int uFlags) {
            FaceEnt rVE = new FaceEnt();
            _vFaceEnts.Add(rVE);
            if (rVE.cDeg != cDegree) {
                rVE.cDeg = cDegree;
                rVE.cEmptyDeg = cDegree;
                rVE.cFaceAttrs = cFaceAttrs;
                rVE.uFlags = uFlags;
                rVE.uAttrMask = uFaceAttrMask;
                rVE.iFVI = _viFaceVtxIndices.Count;
                rVE.iFAI = _viFaceAttrIndices.Count;

                while ((rVE.iFVI + cDegree) > _viFaceVtxIndices.Count) {
                    _viFaceVtxIndices.Add(0);
                }

                if (cFaceAttrs > 0) {
                    while ((rVE.iFAI + cFaceAttrs) > _viFaceAttrIndices.Count) {
                        _viFaceAttrIndices.Add(0);
                    }
                }

                for (int i = rVE.iFVI; i < rVE.iFVI + cDegree; i++) {
                    _viFaceVtxIndices[i] = -1;
                }

                for (int i = rVE.iFAI; i < rVE.iFAI + cFaceAttrs; i++) {
                    _viFaceAttrIndices[i] = -1;
                }
            }
        }

        /**
         *
         * @param iFace
         * @param cDegree
         * @param cFaceAttrs
         * @param pvbFaceAttrMask
         * @param uFlags
         */
        public void newFace(int iFace, int cDegree, int cFaceAttrs, BitVector pvbFaceAttrMask, int uFlags) {
            while (_vFaceEnts.Count <= iFace) {
                _vFaceEnts.Add(new FaceEnt());
            }

            FaceEnt rVE = _vFaceEnts[iFace];
            if (rVE.cDeg != cDegree) {
                rVE.cDeg = cDegree;
                rVE.cEmptyDeg = cDegree;
                rVE.cFaceAttrs = cFaceAttrs;
                rVE.uFlags = uFlags;
                rVE.pvbAttrMask = new BitVector(pvbFaceAttrMask);
                rVE.iFVI = _viFaceVtxIndices.Count;
                rVE.iFAI = _viFaceAttrIndices.Count;

                while ((rVE.iFVI + cDegree) > _viFaceVtxIndices.Count) {
                    _viFaceVtxIndices.Add(0);
                }

                if (cFaceAttrs > 0) {
                    while ((rVE.iFAI + cFaceAttrs) > _viFaceAttrIndices.Count) {
                        _viFaceAttrIndices.Add(0);
                    }
                }

                for (int i = rVE.iFVI; i < (rVE.iFVI + cDegree); i++) {
                    _viFaceVtxIndices[i] = -1;
                }

                for (int i = rVE.iFAI; i < (rVE.iFAI + cFaceAttrs); i++) {
                    _viFaceAttrIndices[i] = -1;
                }
            }
        }

        /**
         *
         * @param iFace
         * @param iAttrSlot
         * @param iFaceAttr
         */
        public void setFaceAttr(int iFace, int iAttrSlot, int iFaceAttr) {
            FaceEnt rVE = _vFaceEnts[iFace];
            _viFaceAttrIndices[rVE.iFAI + iAttrSlot] = iFaceAttr;
        }

        /**
         * Attaches VF face iFace to VF vertex iVtx in the vertex's face slot iFaceSlot.
         * @param  iVtx
         * @param  iFaceSlot
         * @param  iFace
         * @return           ---
         */
        public bool setVtxFace(int iVtx, int iFaceSlot, int iFace) {
            VtxEnt rFE = _vVtxEnts[iVtx];
            _viVtxFaceIndices[rFE.iVFI + iFaceSlot] = iFace;
            return true;
        }

        /**
         * Attaches VF vertex iVtx to VF face iFace in the face's vertex slot iVtxSlot.
         * @param  iFace
         * @param  iVtxSlot
         * @param  iVtx
         * @return          ---
         */
        public bool setFaceVtx(int iFace, int iVtxSlot, int iVtx) {
            FaceEnt rVE = _vFaceEnts[iFace];

            if (_viFaceVtxIndices[rVE.iFVI + iVtxSlot] != iVtx) {
                rVE.cEmptyDeg -= 1;
            }

            _viFaceVtxIndices[rVE.iFVI + iVtxSlot] = iVtx;

            return true;
        }

        /**
         * Searches the list of incident vts to face iFace for iTargVtx and returns the vtx slot at which it is found
         * or -1 if iTargVtx is not found.
         * @param  iFace
         * @param  iTargVtx
         * @return          Vertex slot
         */
        public int findVtxSlot(int iFace, int iTargVtx) {
            FaceEnt rVE = _vFaceEnts[iFace];
            int cDeg = rVE.cDeg;
            int iSlot = -1;
            for (int iVtxSlot = 0; iVtxSlot < cDeg; iVtxSlot++) {
                if (_viFaceVtxIndices[iVtxSlot + rVE.iFVI] == iTargVtx) {
                    iSlot = iVtxSlot;
                    break;
                }
            }

            return iSlot;
        }

        /**
         * Searches the list of incident faces to vertex iVtx for iTargFace and returns the face slot at which it is found
         * or -1 if iTargFace is not found.
         * @param  iVtx
         * @param  iTargFace
         * @return           Face slot
         */
        public int findFaceSlot(int iVtx, int iTargFace) {
            VtxEnt rFE = _vVtxEnts[iVtx];
            for (int iFaceSlot = 0; iFaceSlot < rFE.cVal; iFaceSlot++) {
                if (_viVtxFaceIndices[iFaceSlot + rFE.iVFI] == iTargFace) {
                    return iFaceSlot;
                }
            }

            return -1;
        }

        /**
         *
         * @param  iVtx
         * @return      Vertex flags
         */
        public int vtxFlags(int iVtx) {
            int u = 0;
            if (iVtx >= 0 && iVtx < _vVtxEnts.Count) {
                VtxEnt rFE = _vVtxEnts[iVtx];
                u = rFE.uFlags;
            }

            return u;
        }

        /**
         *
         * @param  iVtx
         * @param  iFace
         * @return       Vertex face attribute
         */
        public int vtxFaceAttr(int iVtx, int iFace) {
            FaceEnt rVE = _vFaceEnts[iFace];
            if (rVE.cFaceAttrs <= 0) {
                return -1;
            }

            int cDeg = rVE.cDeg;
            int iAttrSlot = -1;
            for (int iVtxSlot = 0; iVtxSlot < cDeg; iVtxSlot++) {
                int iSlot = iVtxSlot;
                if (cDeg <= DualVFMesh.cMBits) {
                    if ((rVE.uAttrMask & ((long)1 << iSlot)) != 0) {
                        iAttrSlot++;
                    }
                }
                else {
                    if (rVE.pvbAttrMask.test(iSlot)) {
                        iAttrSlot++;
                    }
                }

                while (iAttrSlot < 0) {
                    iAttrSlot += rVE.cFaceAttrs;
                }

                if (_viFaceVtxIndices[rVE.iFVI + iVtxSlot] == iVtx) {
                    // Keep only explicitly encoded corner normals. Missing corners
                    // use the writer's geometric fallback, as in the validated route.
                    bool explicitCorner = cDeg <= DualVFMesh.cMBits
                        ? (rVE.uAttrMask & ((long)1 << iSlot)) != 0
                        : rVE.pvbAttrMask.test(iSlot);
                    if (!explicitCorner) return -1;
                    return _viFaceAttrIndices[rVE.iFAI + (iAttrSlot % rVE.cFaceAttrs)];
                }
            }

            return -1;
        }

        /**
         *
         * @param  index
         * @return      FaceEnt
         */
        public FaceEnt getFaceEnt(int index) {
            return _vFaceEnts[index];
        }

        /**
         *
         * @return ---
         */
        public int getVVBAttrMasksSize() {
            return _vvbAttrMasks.Count;
        }
    }
}
