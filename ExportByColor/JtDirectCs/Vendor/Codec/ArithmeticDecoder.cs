﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public static class ArithmeticDecoder {
        public class AccumulatedProbabilityCounts {
            /** Probability contexts */
            private Int32ProbabilityContexts _int32ProbabilityContexts;

            /** Symbols counts */
            private List<int> _symbolsCounts;

            /** Accumulated occurence counts */
            //private List<SortedDictionary<int, int>> _entryByAccumCountPerContext;
            private List<List<(int,int)>> _entryByAccumCountPerContext2;
            
            /**
             * Constructor.
             * @param int32ProbabilityContexts Probability contexts
             */
            public AccumulatedProbabilityCounts(Int32ProbabilityContexts int32ProbabilityContexts) {
                _int32ProbabilityContexts = int32ProbabilityContexts;
                _symbolsCounts = new List<int>();
                //_entryByAccumCountPerContext = new List<SortedDictionary<int, int>>();
                _entryByAccumCountPerContext2 = new List<List<(int, int)>>();

                for (int i = 0; i < _int32ProbabilityContexts.int32ProbabilityContextTableEntries.Length; i++) {
                    int accumulatedCount = 0;
                    //_entryByAccumCountPerContext.Add(new SortedDictionary<int, int>());
                    _entryByAccumCountPerContext2.Add(new List<(int, int)>());
                    for (int j = 0; j < _int32ProbabilityContexts.int32ProbabilityContextTableEntries[i].Count; j++) {
                        Int32ProbabilityContextTableEntry int32ProbabilityContextTableEntry = _int32ProbabilityContexts.int32ProbabilityContextTableEntries[i][j];
                        accumulatedCount += (int)int32ProbabilityContextTableEntry.occurrenceCount;
                        //_entryByAccumCountPerContext[i].Add((accumulatedCount - 1), j);
                        _entryByAccumCountPerContext2[i].Add((accumulatedCount - 1, j));
                    }

                    _entryByAccumCountPerContext2[i].Sort((x, y) => x.Item1.CompareTo(y.Item1));
                    _symbolsCounts.Add(accumulatedCount);
                }
            }

            // int lower_bound(List<(int, int)> list, int key) {
            //     var low = 0;
            //     var high = list.Count - 1;
            //     while (low < high) {
            //         var mid = (low + high) / 2;
            //         if (list[mid].Item1 < key)
            //             low = mid + 1;
            //         else
            //             high = mid;
            //     }
            //     
            //     return low;
            // }
            
            int upper_bound(List<(int, int)> list, int key) {
                var low = 0;
                var high = list.Count - 1;
                while (low < high) {
                    var mid = (low + high) / 2;
                    if (list[mid].Item1 <= key)
                        low = mid + 1;
                    else
                        high = mid;
                }
                if (list[low].Item1 <= key)
                    low++;
                return low;
            }
            
            /**
             * Returns the entry and symbol range.
             * @param  contextIndex   Context index
             * @param  rescaledCode   Rescaled code
             * @param  newSymbolRange New symbol range
             * @return                Matching probability context table entry
             */
            public Int32ProbabilityContextTableEntry getEntryAndSymbolRangeByRescaledCode(int contextIndex, int rescaledCode, int[] newSymbolRange) {
                //SortedDictionary<int, int> treeMap = _entryByAccumCountPerContext[contextIndex];
                var treeMap2 = _entryByAccumCountPerContext2[contextIndex];
                
                //int key = treeMap.Keys.FirstOrDefault(k => k > rescaledCode - 1);
                //int value = _entryByAccumCountPerContext[contextIndex][key];

                var (key, value) = treeMap2[upper_bound(treeMap2, rescaledCode - 1)];

                // if (key != key2 || value != value2)
                //     throw new Exception("reconstruct failed.");

                Int32ProbabilityContextTableEntry int32ProbabilityContextTableEntry = _int32ProbabilityContexts.int32ProbabilityContextTableEntries[contextIndex][value];

                newSymbolRange[0] = (int)(key + 1 - int32ProbabilityContextTableEntry.occurrenceCount);
                newSymbolRange[1] = key + 1;
                newSymbolRange[2] = _symbolsCounts[contextIndex];

                return int32ProbabilityContextTableEntry;
            }

            /**
             * Returns the total symbol count
             * @param  contextIndex Context index
             * @return              Total symbol count of the given context
             */
            public int getTotalSymbolCount(int contextIndex) {
                return _symbolsCounts[contextIndex];
            }
        }
        public static int[] Decode(CodecDriver codecDriver) {
            List<int> decodedSymbols = new List<int>();
            AccumulatedProbabilityCounts accumProbCounts = new AccumulatedProbabilityCounts(codecDriver.int32ProbabilityContexts);

            int code = 0x0000;
            int low = 0x0000;
            int high = 0xffff;
            int bitBuffer = 0;
            int bits = 0;
            int symbolCount = codecDriver.SymbolCount;
            int currentContext = 0;
            int[] newSymbolRange = new int[3];
            int outOfBandDataCounter = 0;

            var outOfBandValues = new List<int>();
            if (codecDriver.outOfBandValues != null)
                outOfBandValues.AddRange(codecDriver.outOfBandValues);

            int[] results = codecDriver.GetNextCodeText();
            if (results == null) {
                throw new Exception("ERROR: No more code bytes available!");
            } else {
                bitBuffer = results[0];
                bits = results[1];
            }

            code = (bitBuffer >> 16) & 0xffff;
            bitBuffer <<= 16;
            bits -= 16;

            for (int i = 0; i < symbolCount; i++) {
                int rescaledCode = (((((code - low) + 1) * accumProbCounts.getTotalSymbolCount(currentContext) - 1)) / ((high - low) + 1));
                Int32ProbabilityContextTableEntry int32ProbabilityContextTableEntry = accumProbCounts.getEntryAndSymbolRangeByRescaledCode(currentContext, rescaledCode, newSymbolRange);

                int range = high - low + 1;
                high = low + ((range * newSymbolRange[1]) / newSymbolRange[2] - 1);
                low = low + ((range * newSymbolRange[0]) / newSymbolRange[2]);

                for (; ; ) {
                    if (((~(high ^ low)) & 0x8000) > 0) {
                        //Shift both out if most sign.

                    } else if (((low & 0x4000) > 0) && ((high & 0x4000) == 0)) {
                        code ^= 0x4000;
                        code = code & 0xffff;
                        low &= 0x3fff;
                        low = low & 0xffff;
                        high |= 0x4000;
                        high = high & 0xffff;

                    } else {
                        // Nothing to shift out any more
                        break;
                    }

                    low = (low << 1) & 0xffff;
                    high = (high << 1) & 0xffff;
                    high = (high | 1) & 0xffff; ;
                    code = (code << 1) & 0xffff;

                    if (bits == 0) {
                        results = codecDriver.GetNextCodeText();
                        if (results == null) {
                            throw new Exception("ERROR: No more code bytes available!");
                        } else {
                            bitBuffer = results[0];
                            bits = results[1];
                        }
                    }

                    code = (code | ((bitBuffer >> 31) & 0x00000001));
                    bitBuffer <<= 1;
                    bits--;
                }

                if ((int32ProbabilityContextTableEntry.symbol != -2) || (currentContext <= 0)) {
                    if ((int32ProbabilityContextTableEntry.symbol == -2) && (outOfBandDataCounter >= outOfBandValues.Count)) {
                        throw new Exception("'Out-Of-Band' bitStream missing! Read values: " + i + " / " + symbolCount);
                    }
                    decodedSymbols.Add(((int32ProbabilityContextTableEntry.symbol == -2) && outOfBandDataCounter < outOfBandValues.Count ? outOfBandValues[outOfBandDataCounter++] : int32ProbabilityContextTableEntry.associatedValue));
                }
                currentContext = int32ProbabilityContextTableEntry.nextContext;
            }

            return decodedSymbols.ToArray();
        }
    }

}