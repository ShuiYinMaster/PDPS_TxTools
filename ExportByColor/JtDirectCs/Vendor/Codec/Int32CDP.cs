using DLAT.JTReader;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public enum PredictorType {
        PredLag1 = 0,
        PredLag2 = 1,
        PredStride1 = 2,
        PredStride2 = 3,
        PredStripIndex = 4,
        PredRamp = 5,
        PredXor1 = 6,
        PredXor2 = 7,
        PredNull = 8
    }
    public class Int32CDP {
        public const int CODECTYPE_NULL = 0;
        public const int CODECTYPE_BITLENGTH = 1;
        public const int CODECTYPE_HUFFMAN = 2;
        public const int CODECTYPE_ARITHMETIC = 3;

        public static int[] DecodeBytes(StreamReader data) {
            byte codecType = data.ReadU8();
            int[] decodedSymbols;

            if (codecType == CODECTYPE_NULL) {
                var count = data.ReadI32();
                decodedSymbols = new int[count];
                for (var i = 0; i < count; ++i) {
                    decodedSymbols[i] = data.ReadI32();
                }
                return decodedSymbols;
            }


            Int32ProbabilityContexts int32ProbabilityContexts = null;
            int outOfBandValueCount;
            int codeTextLength;
            int valueElementCount;
            int[] outOfBandValues = new int[0];
            int symbolCount;

            if (codecType == CODECTYPE_HUFFMAN || codecType == CODECTYPE_ARITHMETIC) {
                int32ProbabilityContexts = new Int32ProbabilityContexts(data);
                outOfBandValueCount = data.ReadI32();
                if (outOfBandValueCount > 0)
                    outOfBandValues = DecodeBytes(data);
            }

            codeTextLength = data.ReadI32();
            valueElementCount = data.ReadI32();
            symbolCount = valueElementCount;
            if (int32ProbabilityContexts != null && int32ProbabilityContexts.int32ProbabilityContextTableEntries.Length > 1)
                symbolCount = data.ReadI32();

            int intsToRead = data.ReadI32();
            byte[] codeText = new byte[intsToRead * 4];
            for (int i = 0; i < intsToRead; i++) {
                byte[] buffer = data.ReadBytes(4);
                if(data.jtFile.byteOrder == 0){
                    codeText[i * 4] = buffer[3];
                    codeText[(i * 4) + 1] = buffer[2];
                    codeText[(i * 4) + 2] = buffer[1];
                    codeText[(i * 4) + 3] = buffer[0];
                } else {
                    codeText[i * 4] = buffer[0];
                    codeText[(i * 4) + 1] = buffer[1];
                    codeText[(i * 4) + 2] = buffer[2];
                    codeText[(i * 4) + 3] = buffer[3];
                }
            }


            CodecDriver codecDriver = new CodecDriver {
                codeTextBytes = codeText,
                codeTextLengthInBits = codeTextLength,
                valueElementCount = valueElementCount,
                SymbolCount = symbolCount,
                int32ProbabilityContexts = int32ProbabilityContexts,
                bitSteam = new BitStream(new StreamReader(new MemoryStream(codeText),data.jtFile), codeTextLength),
                bitsRead = 0,
                outOfBandValues = outOfBandValues,
            };

            if(codecType == CODECTYPE_BITLENGTH)
                decodedSymbols = BitlengthDecoder.Decode(codecDriver);
            else if(codecType == CODECTYPE_HUFFMAN)
                decodedSymbols = HuffmanDecoder.Decode(codecDriver);
            else if(codecType == CODECTYPE_ARITHMETIC)
                decodedSymbols = ArithmeticDecoder.Decode(codecDriver);
            else
                throw new NotImplementedException();

            //decodedSymbols = codecType switch {
            //    CODECTYPE_BITLENGTH => BitlengthDecoder.Decode(codecDriver),
            //    CODECTYPE_HUFFMAN => HuffmanDecoder.Decode(codecDriver),
            //    CODECTYPE_ARITHMETIC => ArithmeticDecoder.Decode(codecDriver),
            //    _ => throw new NotImplementedException(),
            //};

            return decodedSymbols;
        }
        public static List<int> ReadVecI32(StreamReader data, PredictorType predictorType) {
            var decodedSymbols = new List<int>();
            switch (data.jtFile.majorVersion) {
                case 8:
                    decodedSymbols.AddRange(DecodeBytes(data));
                    break;
                case 9:
                    decodedSymbols.AddRange(Int32CDP2.DecodeBytes(data));
                    break;
                default:
                    decodedSymbols.AddRange(Int32CDP3.DecodeBytes(data));
                    break;
            }

            var unpackedList = UnpackResiduals(decodedSymbols, predictorType);
            return unpackedList;
        }
        public static List<int> ReadVecU32(StreamReader data, PredictorType predictorType) {
            var unpackedList = ReadVecI32(data, predictorType);
            // for (var i = 0; i < unpackedList.Count; i++) {
            //     var f = BitConverter.ToSingle(BitConverter.GetBytes(unpackedList[i]), 0);
            //     unpackedList[i] = unpackedList[i] & 0xffff;
            // }
            return unpackedList;
        }
        private static int PredictValue(List<int> values, int index, PredictorType predictorType) {
            int v1 = values[index - 1];
            int v2 = values[index - 2];
            int v4 = values[index - 4];

            switch (predictorType) {
                default:
                case PredictorType.PredLag1:
                case PredictorType.PredXor1:
                    return v1;

                case PredictorType.PredLag2:
                case PredictorType.PredXor2:
                    return v2;

                case PredictorType.PredStride1:
                    return (v1 + (v1 - v2));

                case PredictorType.PredStride2:
                    return (v2 + (v2 - v4));

                case PredictorType.PredStripIndex:
                    if (((v2 - v4) < 8) && ((v2 - v4) > -8)) {
                        return (v2 + (v2 - v4));
                    } else {
                        return (v2 + 2);
                    }

                case PredictorType.PredRamp:
                    return index;
            }
        }
        public static List<int> UnpackResiduals(List<int> residuals, PredictorType predictorType) {
            int iPredicted;
            int len = residuals.Count;

            List<int> indexList = new List<int>();

            for (int i = 0; i < len; i++) {
                if (predictorType == PredictorType.PredNull) {
                    indexList.Add(residuals[i]);

                } else {
                    // The first four values are not handeled
                    if (i < 4) {
                        indexList.Add(residuals[i]);
                    } else {
                        // Get a predicted value
                        iPredicted = PredictValue(indexList, i, predictorType);

                        // Decode the residual as the current value XOR predicted
                        if ((predictorType == PredictorType.PredXor1) || (predictorType == PredictorType.PredXor2)) {
                            indexList.Add(residuals[i] ^ iPredicted);

                            // Decode the residual as the current value plus predicted
                        } else {
                            indexList.Add(residuals[i] + iPredicted);
                        }
                    }
                }
            }

            return indexList;
        }
    }
    public class Int32ProbabilityContexts {
        public bool hasOutOfBandValues;
        public List<Int32ProbabilityContextTableEntry>[] int32ProbabilityContextTableEntries;
        public Int32ProbabilityContexts(StreamReader data) {
            hasOutOfBandValues = false;
            switch (data.jtFile.majorVersion) {
                case 8:  ReadMk1(data); break;
                case 9:  ReadMk2(data); break;
                case 10: ReadMk3(data); break;
            }
        }

        public void ReadMk1(StreamReader data) {
            var probabilityContextTableCount = data.ReadU8();
            int32ProbabilityContextTableEntries = new List<Int32ProbabilityContextTableEntry>[probabilityContextTableCount];
            Dictionary<int, int> symbol2AssociatedValue = new Dictionary<int, int>();
            int symbolBits = 0;
            int occurrenceCountBits = 0;
            int valueBits = 0;
            int nextContextBits = 0;
            int minimumValue = 0;

            var bitSteam = new BitStream(data);
            for (int i = 0; i < probabilityContextTableCount; i++) {
                int32ProbabilityContextTableEntries[i] = new List<Int32ProbabilityContextTableEntry>();
                var probabilityContextTableEntryCount = bitSteam.ReadU32(32);
                symbolBits = bitSteam.ReadI32(6);
                occurrenceCountBits = bitSteam.ReadI32(6);
                valueBits = 0;
                if (i == 0) valueBits = bitSteam.ReadI32(6);
                nextContextBits = bitSteam.ReadI32(6);
                if (i == 0) minimumValue = bitSteam.ReadI32(32);

                for (int j = 0; j < probabilityContextTableEntryCount; ++j) {
                    var entry = new Int32ProbabilityContextTableEntry(bitSteam, symbolBits,
                        occurrenceCountBits, valueBits, nextContextBits, minimumValue);
                    if (i == 0)
                        symbol2AssociatedValue.Add(entry.symbol, entry.associatedValue);
                    else
                        entry.associatedValue = symbol2AssociatedValue[entry.symbol];
                    int32ProbabilityContextTableEntries[i].Add(entry);
                    if (entry.symbol == -2)
                        hasOutOfBandValues = true;
                }
            }
        }

        public void ReadMk2(StreamReader data) {
            var bitBuffer = new BitStream(data);

            var probabilityContextTableEntryCount = bitBuffer.ReadI32(16);
            var numberSymbolBits = bitBuffer.ReadI32(6);
            var numberOccurrenceCountBits = bitBuffer.ReadI32(6);
            var numberValueBits = bitBuffer.ReadI32(6);
            var minValue = bitBuffer.ReadI32(32);

            int32ProbabilityContextTableEntries = new List<Int32ProbabilityContextTableEntry>[1];
            for(var i = 0; i < int32ProbabilityContextTableEntries.Length; i++){
                int32ProbabilityContextTableEntries[i] = new List<Int32ProbabilityContextTableEntry>();

                for(var j = 0; j < probabilityContextTableEntryCount; j++) {
                    var int32ProbabilityContextTableEntry = new
                        Int32ProbabilityContextTableEntry(bitBuffer, numberSymbolBits, numberOccurrenceCountBits,
                            numberValueBits, -1, minValue);
	
                    int32ProbabilityContextTableEntries[i].Add(int32ProbabilityContextTableEntry);
                    if (int32ProbabilityContextTableEntry.symbol == -2)
                        hasOutOfBandValues = true;
                }
            }

            // var bitsToSkip = (int)bitBuffer.Position % 8;
            // if(bitsToSkip > 0){
            //     bitBuffer.ReadI32((8 - bitsToSkip));
            // }
        }
        public void ReadMk3(StreamReader data) {
            var bitBuffer = new BitStream(data);

            var probabilityContextTableEntryCount = bitBuffer.ReadU32(16);
            var numberOccurrenceCountBits = bitBuffer.ReadU32(6);
            var numberValueBits = bitBuffer.ReadU32(7);
            var minValue = bitBuffer.ReadI32(32);
            //v10 int, v10.5 uint

            int32ProbabilityContextTableEntries = new List<Int32ProbabilityContextTableEntry>[1];
            for(var i = 0; i < int32ProbabilityContextTableEntries.Length; i++){
                int32ProbabilityContextTableEntries[i] = new List<Int32ProbabilityContextTableEntry>();

                for(var j = 0; j < probabilityContextTableEntryCount; j++) {
                    var int32ProbabilityContextTableEntry = new
                        Int32ProbabilityContextTableEntry(bitBuffer, -1, numberOccurrenceCountBits,
                            numberValueBits, -1, minValue);
                    int32ProbabilityContextTableEntries[i].Add(int32ProbabilityContextTableEntry);
                    if (int32ProbabilityContextTableEntry.symbol == -2)
                        hasOutOfBandValues = true;
                }
            }

            // var bitsToSkip = (int)bitBuffer.Position % 8;
            // if(bitsToSkip > 0){
            //     bitBuffer.ReadI32((8 - bitsToSkip));
            // }
        }
    }
    public class Int32ProbabilityContextTableEntry {
        public int symbol;
        public int occurrenceCount;
        public int associatedValue;
        public int nextContext;
        public Int32ProbabilityContextTableEntry(BitStream bitStream,
            int symbolBits, int occurrenceCountBits,
            int valueBits, int nextContextBits, int minValue) {
            if(symbolBits != -1)
                symbol = bitStream.ReadU32(symbolBits) - 2;
            else {
                symbol = (bitStream.ReadI32(1) == 0 ? 0 : -2);
            }
            occurrenceCount = bitStream.ReadU32(occurrenceCountBits);
            associatedValue = bitStream.ReadU32(valueBits) + minValue;
            if(nextContextBits != -1)
                nextContext = bitStream.ReadU32(nextContextBits);
        }
        
        
    }
    public class CodecDriver {
        public byte[] codeTextBytes;
        public int codeTextLengthInBits;
        public int valueElementCount;

        private int _symbolCount;

        public int SymbolCount {
            set => _symbolCount = value;
            get {
                if(_symbolCount == -1){
                    return valueElementCount;
                }

                return (int32ProbabilityContexts.int32ProbabilityContextTableEntries.Length <= 1) ? valueElementCount : _symbolCount;
            }
        }
        public Int32ProbabilityContexts int32ProbabilityContexts;
        public BitStream bitSteam;
        public int bitsRead;
        public int[] outOfBandValues;

        public int[] GetNextCodeText() {
            int nBits = Math.Min(32, codeTextLengthInBits - bitsRead);
            int uCodeText = bitSteam.ReadU32(nBits);
            if (nBits < 32)
                uCodeText <<= (32 - nBits);
            bitsRead += nBits;
            return new int[] { uCodeText, nBits };
        }

    }


}
