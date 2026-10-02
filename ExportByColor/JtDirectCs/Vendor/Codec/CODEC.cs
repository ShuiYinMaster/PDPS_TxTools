using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using SharpCompress.Compressors.Xz;

namespace DLAT.JTReader {
    public static class CODEC {
        public static List<float> Dequantize(List<int> vertexCoordinates, float[] vertexRange, int numberOfBits) {
            float minimum = vertexRange[0];
            float maximum = vertexRange[1];
            long maxCode = 0xffffffff;

            if (numberOfBits < 32) {
                maxCode = 0x1 << numberOfBits;
            }

            float encodeMultiplier = maxCode / (maximum - minimum);

            var dequantizesVertices = new List<float>();
            for (int i = 0; i < vertexCoordinates.Count; i++) {
                dequantizesVertices.Add((((vertexCoordinates[i] - 0.5f) / encodeMultiplier + minimum)));
            }
            return dequantizesVertices;
        }
        public class DeeringNormalCodec {
            public class DeeringLookupEntry {
                /** cosTheta */
                private double _cosTheta;

                /** sinTheta */
                private double _sinTheta;

                /** cosPsi */
                private double _cosPsi;

                /** sinPsi */
                private double _sinPsi;

                /**
                 * Constructor.
                 * @param cosTheta cosTheta
                 * @param sinTheta sinTheta
                 * @param cosPsi   cosPsi
                 * @param sinPsi   sinPsi
                 */
                public DeeringLookupEntry(double cosTheta, double sinTheta, double cosPsi, double sinPsi) {
                    _cosTheta = cosTheta;
                    _sinTheta = sinTheta;
                    _cosPsi = cosPsi;
                    _sinPsi = sinPsi;
                }

                /**
                 * Returns the CosTheta.
                 * @return CosTheta
                 */
                public double getCosTheta() {
                    return _cosTheta;
                }

                /**
                 * Returns the SinTheta.
                 * @return SinTheta
                 */
                public double getSinTheta() {
                    return _sinTheta;
                }

                /**
                 * Returns the CosPsi.
                 * @return CosPsi
                 */
                public double getCosPsi() {
                    return _cosPsi;
                }

                /**
                 * Returns the SinPsi.
                 * @return SinPsi
                 */
                public double getSinPsi() {
                    return _sinPsi;
                }
            }
            public class DeeringNormalLookupTable {
                /** List of CosTheta's */
                private double[] _cosTheta;

                /** List of SinTheta's */
                private double[] _sinTheta;

                /** List of CosPsi's */
                private double[] _cosPsi;

                /** List of SinPsi's */
                private double[] _sinPsi;

                /**
                 * Constructor.
                 */
                public DeeringNormalLookupTable() {
                    int tableSize = 256;
                    _cosTheta = new double[tableSize + 1];
                    _sinTheta = new double[tableSize + 1];
                    _cosPsi = new double[tableSize + 1];
                    _sinPsi = new double[tableSize + 1];

                    double psiMax = 0.615479709;

                    for (int i = 0; i <= tableSize; i++) {
                        double theta = Math.Asin(Math.Tan(psiMax * (tableSize - i) / tableSize));
                        double psi = psiMax * (i / tableSize);
                        _cosTheta[i] = Math.Cos(theta);
                        _sinTheta[i] = Math.Sin(theta);
                        _cosPsi[i] = Math.Cos(psi);
                        _sinPsi[i] = Math.Sin(psi);
                    }
                }

                /**
                 * Returns a deering lookup entry.
                 * @param  theta        Theta
                 * @param  psi          Psi
                 * @param  numberOfBits Number of bits
                 * @return              Deering lookup entry
                 */
                public DeeringLookupEntry lookupThetaPsi(long theta, long psi, long numberOfBits) {
                    long offset = 8 - numberOfBits;
                    long offTheta = (theta << (int)offset) & 0xFFFFFFFFL;
                    long offPsi = (psi << (int)offset) & 0xFFFFFFFFL;

                    return new DeeringLookupEntry(_cosTheta[(int)offTheta], _sinTheta[(int)offTheta], _cosPsi[(int)offPsi], _sinPsi[(int)offPsi]);
                }
            }
            /** Number of bits */
            private long _numberOfBits;

            /** Deering normal lookup table */
            private static DeeringNormalLookupTable _deeringNormalLookupTable;

            /**
             * Constructor.
             * @param numberOfBits Number of bits
             */
            public DeeringNormalCodec(long numberOfBits) {
                if (_deeringNormalLookupTable == null) {
                    _deeringNormalLookupTable = new DeeringNormalLookupTable();
                }
                _numberOfBits = numberOfBits;
            }

            /**
             * Converts a code to a normal vector.
             * @param  sextant Sextant
             * @param  octant  Octant
             * @param  theta   Theta
             * @param  psi     Psi
             * @return         Normal vector
             */
            public DirF64 convertCodeToVec(long sextant, long octant, long theta, long psi) {
                theta += (sextant & 1);

                DeeringLookupEntry lookupEntry = _deeringNormalLookupTable.lookupThetaPsi(theta, psi, _numberOfBits);

                double x, y, z;
                double xx = x = lookupEntry.getCosTheta() * lookupEntry.getCosPsi();
                double yy = y = lookupEntry.getSinPsi();
                double zz = z = lookupEntry.getSinTheta() * lookupEntry.getCosPsi();

                switch ((int)sextant) {
                    case 0:
                        break;
                    case 1:
                        z = xx;
                        x = zz;
                        break;
                    case 2:
                        z = xx;
                        x = yy;
                        y = zz;
                        break;
                    case 3:
                        y = xx;
                        x = yy;
                        break;
                    case 4:
                        y = xx;
                        z = yy;
                        x = zz;
                        break;
                    case 5:
                        z = yy;
                        y = zz;
                        break;
                }

                if ((octant & 0x4) == 0) {
                    x = -x;
                }

                if ((octant & 0x2) == 0) {
                    y = -y;
                }

                if ((octant & 0x1) == 0) {
                    z = -z;
                }

                return new DirF64(x, y, z);
            }


            public (uint,uint,uint,uint) unpackCode(uint code, int numBits) {
                uint outSextant;
                uint outOctant;
                uint outTheta;
                uint outPsi;
                var mask = (1u << numBits) - 1u;
                outSextant = (code >> (numBits+numBits+3)) & 0x7;
                outOctant = (code >> (numBits+numBits)) & 0x7;
                outTheta = (code >> (numBits)) & mask;
                outPsi = (code) & mask;
                return (outSextant,outOctant,outTheta,outPsi);
            } 
            
        }
        public static readonly (string,int)[] fieldTypeData = new (string,int)[]{
            ("empty",0),
            ("U8",1), ("U8",2), ("U8",3),  ("U8",4),
            ("I8",1), ("I8",2), ("I8",3),  ("I8",4),
            ("U16",1), ("U16",2), ("U16",3),  ("U16",4),
            ("I16",1), ("I16",2), ("I16",3),  ("I16",4),
            ("U32",1), ("U32",2), ("U32",3),  ("U32",4),
            ("I32",1), ("I32",2), ("I32",3),  ("I32",4),
            ("U64",1), ("U64",2), ("U64",3),  ("U64",4),
            ("I64",1), ("I64",2), ("I64",3),  ("I64",4),
            ("F32",1), ("F32",2), ("F32",3),  ("F32",4),
            ("F32",4), ("F32",9), ("F32",16), ("F64",1),
            ("F64",2), ("F64",3), ("F64",4),  ("F64",4),
            ("F64",9), ("F64",16)
        };

        public static List<float> IntArrayToFloatArray(List<int> values) {
            var res = new List<float>();
            foreach (var val in values) 
                res.Add(BitConverter.ToSingle(BitConverter.GetBytes(val), 0));
            return res;
        }
        
        public static Stream DecompressZLIB(Stream compressed) {
            var deflate = new DeflateStream(compressed, CompressionMode.Decompress);
            var decompressed = new MemoryStream();
            deflate.CopyTo(decompressed);
            deflate.Dispose();
            decompressed.Position = 0;
            return decompressed;
        }
        
        public static Stream DecompressLZMA(Stream compressed) {
            var xz = new XZStream(compressed);
            var decompressed = new MemoryStream();
            xz.CopyTo(decompressed);
            xz.Dispose();
            decompressed.Position = 0;
            return decompressed;
        }
        
    }
}