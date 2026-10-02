using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public static class BitlengthDecoder {
        public static int[] Decode(CodecDriver codecDriver) {
            var encodedBits = codecDriver.bitSteam;
            int bitFieldWith = 0;
            var decodedSymbols = new List<int>();

            while (encodedBits.Position < encodedBits.Length) {
                if (encodedBits.ReadU32(1) == 0) {
                    // Decode symbol with same bit field length
                    int decodedSymbol = -1;
                    if (bitFieldWith == 0) {
                        decodedSymbol = 0;
                    } else {
                        decodedSymbol = encodedBits.ReadU32(bitFieldWith);
                        decodedSymbol <<= (32 - bitFieldWith);
                        decodedSymbol >>= (32 - bitFieldWith);
                    }
                    decodedSymbols.Add(decodedSymbol);

                } else {
                    // Adjust bit field length
                    int adjustmentBit = encodedBits.ReadU32(1);
                    do {
                        if (adjustmentBit == 1) {
                            bitFieldWith += 2;
                        } else {
                            bitFieldWith -= 2;
                        }
                    } while (encodedBits.ReadU32(1) == adjustmentBit);

                    // Decode symbol with new bit field length
                    int decodedSymbol = -1;
                    if (bitFieldWith == 0) {
                        decodedSymbol = 0;
                    } else {
                        decodedSymbol = encodedBits.ReadU32(bitFieldWith);
                        decodedSymbol <<= (32 - bitFieldWith);
                        decodedSymbol >>= (32 - bitFieldWith);
                    }
                    decodedSymbols.Add(decodedSymbol);
                }
            }

            return decodedSymbols.ToArray();
        }

        public static int[] Decode2(CodecDriver codecDriver) {
            var encodedBits = codecDriver.bitSteam;
            var decodedSymbols = new List<int>();

            var expectedValues = codecDriver.valueElementCount;
            var totalNumberOfBits = codecDriver.codeTextLengthInBits;

            if (encodedBits.ReadU32(1) == 0) { // Handle fixed width
                var numberOfBitsFromMinSymbol = encodedBits.ReadI32(6);
                var numberOfBitsFromMaxSymbol = encodedBits.ReadI32(6);
                var minSymbol = encodedBits.ReadI32(numberOfBitsFromMinSymbol);
                var maxSymbol = encodedBits.ReadI32(numberOfBitsFromMaxSymbol);

                int bitFieldWith = GetBitFieldWidth((uint)(maxSymbol - minSymbol));

                // Read each fixed-width field and output the value
                while ((encodedBits.Position < totalNumberOfBits) || (decodedSymbols.Count < expectedValues)) {
                    int decodedSymbol = encodedBits.ReadU32(bitFieldWith);
                    decodedSymbol += minSymbol;
                    decodedSymbols.Add(decodedSymbol);
                }
            }
            else { // Handle variable width
                // Write out the mean value
                int iMean = encodedBits.ReadI32(32);
                int cBlkValBits = encodedBits.ReadU32(3);
                int cBlkLenBits = encodedBits.ReadU32(3);

                // Set the initial field-width
                int cMaxFieldDecr = -(1 << (cBlkValBits - 1)); // -ve number
                int cMaxFieldIncr = (1 << (cBlkValBits - 1)) - 1; // +ve number
                int cCurFieldWidth = 0;
                int cDeltaFieldWidth;
                int cRunLen;

                for (int i = 0; i < expectedValues;) {
                    // Adjust the current field width to the target field width
                    do {
                        cDeltaFieldWidth = encodedBits.ReadI32(cBlkValBits);
                        cCurFieldWidth += cDeltaFieldWidth;
                    } while ((cDeltaFieldWidth == cMaxFieldDecr) || (cDeltaFieldWidth == cMaxFieldIncr));

                    // Read in the run length
                    cRunLen = encodedBits.ReadU32(cBlkLenBits);

                    // Read in the data bits for the run
                    for (int j = i; j < i + cRunLen; j++) {
                        decodedSymbols.Add(encodedBits.ReadI32(cCurFieldWidth) + iMean);
                    }

                    // Advance to the end of the run
                    i += cRunLen;
                }
            }
            if((encodedBits.Position != totalNumberOfBits) || (decodedSymbols.Count != expectedValues)){
                throw new Exception("BithlengthCodec2 didn't consume all bits!");
            }
            return decodedSymbols.ToArray();
        }
        private static int nibblerGet(BitStream data, int bitWidth)
        {
            var res = 0;
            int bMoreBits, uTmp, cNibbles = 0;
            do {
                int temp = data.ReadU32(bitWidth);
                temp <<= (cNibbles * bitWidth);
                res |= temp;
                //res <<= bitWidth;
                //res |= temp;
                bMoreBits = data.ReadU32(1);
                cNibbles++;
            } while (bMoreBits != 0);
            var sw = cNibbles * bitWidth;
            if (sw < 32) {
                res <<= 32 - sw;
                res >>= 32 - sw;
            }
            return res;
        }
        public static int[] Decode3(CodecDriver codecDriver) {
            var encodedBits = codecDriver.bitSteam;
            var decodedSymbols = new List<int>();

            var expectedValues = codecDriver.valueElementCount;
            var totalNumberOfBits = codecDriver.codeTextLengthInBits;

            if (encodedBits.ReadU32(1) == 0) { // Handle fixed width
                var minSymbol = nibblerGet(encodedBits, 4);
                var maxSymbol = nibblerGet(encodedBits, 4);

                var bitFieldWith = GetBitFieldWidth((uint)(maxSymbol - minSymbol));

                // Read each fixed-width field and output the value
                while ((encodedBits.Position < totalNumberOfBits) || (decodedSymbols.Count < expectedValues)) {
                    int decodedSymbol = encodedBits.ReadU32(bitFieldWith);
                    decodedSymbol += minSymbol;
                    decodedSymbols.Add(decodedSymbol);
                }
            }
            else { // Handle variable width
                // Write out the mean value
                int iMean = nibblerGet(encodedBits, 4);
                int cBlkValBits = 4;
                int cBlkLenBits = 4;

                // Set the initial field-width
                int cMaxFieldDecr = -(1 << (cBlkValBits - 1)); // -ve number
                int cMaxFieldIncr = (1 << (cBlkValBits - 1)) - 1; // +ve number
                int cCurFieldWidth = 0;
                int cDeltaFieldWidth;
                int cRunLen;

                for (int i = 0; i < expectedValues;) {
                    // Adjust the current field width to the target field width
                    do {
                        cDeltaFieldWidth = encodedBits.ReadI32(cBlkValBits);
                        cCurFieldWidth += cDeltaFieldWidth;
                    } while ((cDeltaFieldWidth == cMaxFieldDecr) || (cDeltaFieldWidth == cMaxFieldIncr));

                    // Read in the run length
                    cRunLen = encodedBits.ReadU32(cBlkLenBits);

                    // Read in the data bits for the run
                    for (int j = i; j < i + cRunLen; j++) {
                        decodedSymbols.Add(encodedBits.ReadI32(cCurFieldWidth) + iMean);
                    }

                    // Advance to the end of the run
                    i += cRunLen;
                }
            }
            if((encodedBits.Position != totalNumberOfBits) || (decodedSymbols.Count != expectedValues)){
                throw new Exception("BithlengthCodec3 didn't consume all bits!");
            }
            return decodedSymbols.ToArray();
        }
        
        // private static int GetBitFieldWidth(int symbol){
        //     // Note: This calculation assumes that iSymbol is positive!
        //     symbol = Math.Abs(symbol);
        //
        //     // Zero is the only number that can be encoded in a single bit with this scheme, so this
        //     // method returns 0 bits for a symbol value of zero!
        //     if(symbol == 0){
        //         return 0;
        //     }
        //
        //     int i;
        //     int bitFieldWidth;
        //     for(i = 1, bitFieldWidth = 0; (i <= symbol) && (bitFieldWidth < 31); i += i, bitFieldWidth++);
        //     return bitFieldWidth;
        // }
        
        private static int GetBitFieldWidth(uint symbol)
        {
            symbol = symbol ^ (symbol >> 31);
            return (int)(32 - Nlz((symbol)));
        }
        private static uint Nlz(uint x) {
            x = x | (x >> 1);
            x = x | (x >> 2);
            x = x | (x >> 4);
            x = x | (x >> 8);
            x = x | (x >> 16);
            return PopCount(~x);
        }
        private static uint PopCount(uint x) {
            x = x - ((x >> 1) & 0x55555555);
            x = (x & 0x33333333) + ((x >> 2) & 0x33333333);
            x = (x + (x >> 4)) & 0x0f0f0f0f;
            x = x + (x >> 8);
            x = x + (x >> 16);
            return x & 0x3f;
        }
        
    }

}
