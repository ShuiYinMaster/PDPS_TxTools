using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DLAT.JTReader {
    /// <summary>
    /// Int32CDP for jt version 10+
    /// from official document this class should be named Int32CDP
    /// but to identify with Int32CDP from version 8,9
    /// I named it to Int32CDP3
    /// </summary>
    public class Int32CDP3 {
        public const int CODECTYPE_NULL = 0;
        public const int CODECTYPE_BITLENGTH = 1;
        public const int CODECTYPE_ILLEGAL = 2;
        public const int CODECTYPE_ARITHMETIC = 3;
        public const int CODECTYPE_CHOPPER = 4;
        public const int CODECTYPE_MOVE_TO_FRONT = 5;

        public static int[] DecodeBytes(StreamReader data) {
            var decodedSymbols = new List<int>();
            var valueCount = data.ReadI32();
            if (valueCount <= 0)
                return decodedSymbols.ToArray();

            var codecType = data.ReadU8();

            //Debug.Log("#rDecode#w:" + valueCount + " type:" + codecType, 2);
            
            if ((codecType != 0) && (codecType != 1) && (codecType != 3) 
                && (codecType != 4)&& (codecType != 5))
                throw new Exception("Found invalid codec type: " + codecType);

            if (codecType == CODECTYPE_CHOPPER) {
                var chopBits = data.ReadU8();
                //if (chopBits == 0)
                //    return DecodeBytes(data);
                var valueBias = data.ReadI32();
                var valueSpanBits = data.ReadU8();
                var choppedMSBData = DecodeBytes(data);
                var choppedLSBData = DecodeBytes(data);
                for (int i = 0; i < choppedMSBData.Length; ++i)
                    decodedSymbols.Add((choppedLSBData[i] | (choppedMSBData[i] << (valueSpanBits - chopBits))) +
                                       valueBias);
                return decodedSymbols.ToArray();
            }

            if (codecType == CODECTYPE_MOVE_TO_FRONT) {
                var choppedMSBData = DecodeBytes(data); 
                var windowOffsets = DecodeBytes(data);
                decodedSymbols.AddRange(MoveToFront.Decode(choppedMSBData, windowOffsets));
                return decodedSymbols.ToArray();
            }
            
            int intsToRead = 0;
            if (codecType == CODECTYPE_NULL) {
                intsToRead = data.ReadI32() / 4;
                for (int i = 0; i < intsToRead; i++)
                    decodedSymbols.Add(data.ReadI32());
                return decodedSymbols.ToArray();
            }

            var codeTextLength = data.ReadI32();
            intsToRead = codeTextLength / 32 + (codeTextLength % 32 == 0 ? 0 : 1);
            var codeText = new byte[intsToRead * 4];
            for (int i = 0; i < intsToRead; i++) {
                byte[] buffer = data.ReadBytes(4);
                if (data.jtFile.byteOrder == 0) {
                    codeText[i * 4] = buffer[3];
                    codeText[(i * 4) + 1] = buffer[2];
                    codeText[(i * 4) + 2] = buffer[1];
                    codeText[(i * 4) + 3] = buffer[0];
                }
                else {
                    codeText[i * 4] = buffer[0];
                    codeText[(i * 4) + 1] = buffer[1];
                    codeText[(i * 4) + 2] = buffer[2];
                    codeText[(i * 4) + 3] = buffer[3];
                }
            }

            Int32ProbabilityContexts int32ProbabilityContexts = null;
            int[] outOfBandValues = new int[0];
            
            if (codecType == CODECTYPE_ARITHMETIC) {
                int32ProbabilityContexts = new Int32ProbabilityContexts(data);
                
                if(int32ProbabilityContexts.hasOutOfBandValues)
                    outOfBandValues = DecodeBytes(data);
                if (codeTextLength == 0 && outOfBandValues.Length == valueCount)
                    return outOfBandValues;
            }

            CodecDriver codecDriver = new CodecDriver {
                codeTextBytes = codeText,
                codeTextLengthInBits = codeTextLength,
                valueElementCount = valueCount,
                SymbolCount = -1,
                int32ProbabilityContexts = int32ProbabilityContexts,
                bitSteam = new BitStream(new StreamReader(new MemoryStream(codeText),data.jtFile), codeTextLength),
                bitsRead = 0,
                outOfBandValues = outOfBandValues,
            };

            switch (codecType) {
                case CODECTYPE_BITLENGTH:
                    decodedSymbols.AddRange(BitlengthDecoder.Decode3(codecDriver));
                    break;

                case CODECTYPE_ARITHMETIC:
                    decodedSymbols.AddRange(ArithmeticDecoder.Decode(codecDriver));
                    break;
            }

            if (decodedSymbols.Count != valueCount) {
                throw new Exception("Codec produced wrong number of symbols: " + decodedSymbols.Count + " / " +
                                    valueCount);
            }

            return decodedSymbols.ToArray();
        }

        // public static List<int> ReadVecI32(Stream data, PredictorType predictorType) {
        //     var decodedSymbols = DecodeBytes(data).ToList();
        //     var unpackedSymbols = Int32CDP.UnpackResiduals(decodedSymbols, predictorType);
        //     return unpackedSymbols;
        // }
        //
        // public static List<int> ReadVecU32(Stream data, PredictorType predictorType) {
        //     List<int> decodedSymbols = DecodeBytes(data).ToList();
        //     List<int> unpackedList = Int32CDP.UnpackResiduals(decodedSymbols, predictorType);
        //
        //     for (int i = 0; i < unpackedList.Count; i++)
        //         unpackedList[i] &= 0xffff;
        //     return unpackedList;
        // }
    }
}