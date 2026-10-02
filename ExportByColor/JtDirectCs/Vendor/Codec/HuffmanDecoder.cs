using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public static class HuffmanDecoder {
        public class HuffmanCodeData {
            public int symbol;
            public int codeLength;
            public int bitCode;
        }
        public class HuffmanTreeNode {
            public int symbolCount;
            public HuffmanTreeNode left;
            public HuffmanTreeNode right;
            public HuffmanCodeData huffmanCodeData;
            public int associatedValue;

            public HuffmanTreeNode() {
                huffmanCodeData = new HuffmanCodeData();
                symbolCount = 0;
                associatedValue = 0;
            }
            
            public bool IsLesser(HuffmanTreeNode node) {
                return symbolCount < node.symbolCount;
            }
            public bool IsLeaf() {
                return left == null && right == null;
            }
        }
        public class HuffmanCodecContext {
            public int length;
            public long code;
            public List<HuffmanCodeData> huffmanCodeDatas = new List<HuffmanCodeData>();
        }

        public class HuffmanHeap {
            public List<HuffmanTreeNode> heap = new List<HuffmanTreeNode>();
            public void Add(HuffmanTreeNode node) {
                heap.Add(node);
                int i = heap.Count;
                while ((i != 1) && (heap[(i / 2) - 1].symbolCount > node.symbolCount)) {
                    // overwrite i with the parent of i
                    heap[i - 1] = heap[(i / 2) - 1];
                    // Parent of i is a new i
                    i = i / 2;
                }
                heap[(i - 1)] = node;
            }

            private void remove() {
                if (heap.Count == 0) {
                    return;
                }

                int size = heap.Count;
                HuffmanTreeNode y = heap[size - 1];   // Re-insert the last element, because the list will be shortned by one
                int i = 1;                              // i is current "parent", which shall be removed / overwritten
                int ci = 2;                             // ci is current "child"
                size -= 1;                              // The new size is decremented by one

                while (ci <= size) {
                    // Go to the left or to the right? Use the "smaller" element
                    if ((ci < size) && (heap[ci - 1].symbolCount > heap[ci].symbolCount)) {
                        ci += 1;
                    }

                    // If the new "last" element already fits (it has to be smaller than the smallest
                    // childs of i), than break the loop
                    if (y.symbolCount < heap[ci - 1].symbolCount) {
                        break;

                        // Otherwise move the "child" up to the "parent" and continue with i at ci
                    } else {
                        heap[i - 1] = heap[ci - 1];
                        i = ci;
                        ci *= 2;
                    }
                }

                // Set "last" element to the current position i
                heap[(i - 1)] = y;

                // Resize node list by -1
                heap.RemoveAt(heap.Count - 1);
            }
            public HuffmanTreeNode GetTop() {
                if (heap.Count == 0) {
                    return null;
                }

                HuffmanTreeNode huffTreeNode = heap[0];
                remove();
                return huffTreeNode;
            }
        }



        private static HuffmanTreeNode buildHuffmanTree(List<Int32ProbabilityContextTableEntry> int32ProbabilityContextTableEntries) {
            HuffmanHeap huffHeap = new HuffmanHeap();

            HuffmanTreeNode huffTreeNode = null;

            // Initialize all the nodes and add them to the heap.
            int numberOfEntries = int32ProbabilityContextTableEntries.Count;
            for (int i = 0; i < numberOfEntries; i++) {
                Int32ProbabilityContextTableEntry int32ProbabilityContextTableEntry = int32ProbabilityContextTableEntries[i];
                huffTreeNode = new HuffmanTreeNode();
                huffTreeNode.huffmanCodeData.symbol = (int)int32ProbabilityContextTableEntry.symbol;
                huffTreeNode.symbolCount = int32ProbabilityContextTableEntry.occurrenceCount;
                huffTreeNode.associatedValue = (int)int32ProbabilityContextTableEntry.associatedValue;

                huffTreeNode.left = null;
                huffTreeNode.right = null;

                huffHeap.Add(huffTreeNode);
            }

            HuffmanTreeNode newNode1 = null;
            HuffmanTreeNode newNode2 = null;

            while (huffHeap.heap.Count > 1) {
                // Get the two lowest-frequency nodes.
                newNode1 = huffHeap.GetTop();
                newNode2 = huffHeap.GetTop();

                //Combine the low-freq nodes into one node.
                huffTreeNode = new HuffmanTreeNode();
                huffTreeNode.huffmanCodeData.symbol = BitConverter.ToInt32(new byte[] { 0xde, 0xad, 0xbe, 0xef }, 0);
                huffTreeNode.left = newNode1;
                huffTreeNode.right = newNode2;
                huffTreeNode.symbolCount = newNode1.symbolCount + newNode2.symbolCount;

                //Add the new node to the node list
                huffHeap.Add(huffTreeNode);
            }

            // Set the root node
            return huffHeap.GetTop();
        }

        private static void assignCodeToTree(HuffmanTreeNode huffTreeNode, HuffmanCodecContext huffCodecContext) {
            if (huffTreeNode.left != null) {
                huffCodecContext.code = (huffCodecContext.code << 1) & 0xffff;
                huffCodecContext.code = (huffCodecContext.code | 1) & 0xffff;
                huffCodecContext.length = huffCodecContext.length + 1;
                assignCodeToTree(huffTreeNode.left, huffCodecContext);
                huffCodecContext.length = huffCodecContext.length - 1;
                huffCodecContext.code = huffCodecContext.code >> 1;
            }

            if (huffTreeNode.right != null) {
                huffCodecContext.code = (huffCodecContext.code << 1) & 0xffff;
                huffCodecContext.length = huffCodecContext.length + 1;
                assignCodeToTree(huffTreeNode.right, huffCodecContext);
                huffCodecContext.length = huffCodecContext.length - 1;
                huffCodecContext.code = huffCodecContext.code >> 1;
            }

            if (huffTreeNode.right != null) {
                return;
            }

            // Set the code and its length for the node.
            huffTreeNode.huffmanCodeData.bitCode = (int)huffCodecContext.code;
            huffTreeNode.huffmanCodeData.codeLength = huffCodecContext.length;

            // Setup the internal symbol look-up table.
            huffCodecContext.huffmanCodeDatas.Insert(0, new HuffmanCodeData {
                symbol = huffTreeNode.huffmanCodeData.symbol,
                bitCode = huffTreeNode.huffmanCodeData.bitCode,
                codeLength = huffTreeNode.huffmanCodeData.codeLength
            });
        }

        private static List<int> codeTextToSymbols(CodecDriver codecDriver, List<HuffmanTreeNode> huffTreeNodes) {
            List<int> decodedSymbols = new List<int>();

            BitStream encodedBits = codecDriver.bitSteam;
            int outOfBandDataCounter = 0;
            List<int> outOfBandValues = new List<int>(codecDriver.outOfBandValues);

            foreach (var huffTreeRootNode in huffTreeNodes) {
                HuffmanTreeNode huffTreeNode = huffTreeRootNode;
                while (encodedBits.Position < codecDriver.codeTextLengthInBits) {
                    huffTreeNode = (encodedBits.ReadU32(1) == 1) ? huffTreeNode.left : huffTreeNode.right;

                    // If the node is a leaf, output a symbol and restart
                    if (huffTreeNode.IsLeaf()) {
                        int symbol = huffTreeNode.huffmanCodeData.symbol;
                        if (symbol == -2) {
                            if (outOfBandDataCounter < outOfBandValues.Count) {
                                decodedSymbols.Add(outOfBandValues[outOfBandDataCounter++]);
                            } else {
                                throw new Exception("'Out-Of-Band' bitStream missing!");
                            }
                        } else {
                            decodedSymbols.Add(huffTreeNode.associatedValue);
                        }
                        huffTreeNode = huffTreeRootNode;
                    }
                }
            }

            return decodedSymbols;
        }

        public static int[] Decode(CodecDriver codecDriver) {
            List<HuffmanTreeNode> huffmanRootNodes = new List<HuffmanTreeNode>();
            int numberOfProbabilityContexts = codecDriver.int32ProbabilityContexts.int32ProbabilityContextTableEntries.Length;
            List<HuffmanCodecContext> vHuffCntx = new List<HuffmanCodecContext>();
            for (int i = 0; i < numberOfProbabilityContexts; i++) {
                // Get the i'th probability context
                List<Int32ProbabilityContextTableEntry> probabilityContextEntries = codecDriver.int32ProbabilityContexts.int32ProbabilityContextTableEntries[i];

                // Create Huffman tree from probability context
                HuffmanTreeNode rootNode = buildHuffmanTree(probabilityContextEntries);

                // Assign Huffman codes
                vHuffCntx.Add(new HuffmanCodecContext());
                assignCodeToTree(rootNode, vHuffCntx[i]);

                // Store the completed Huffman tree
                huffmanRootNodes.Insert(i, rootNode);
            }

            // Convert codetext to symbols
            return codeTextToSymbols(codecDriver, huffmanRootNodes).ToArray();
        }
    }

}
