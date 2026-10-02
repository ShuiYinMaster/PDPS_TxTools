using System.Collections.Generic;

namespace DLAT.JTReader {
    public class BitVector : List<long> {
        /** ID of the serializable class */
        private static long serialVersionUID = 73647484L;

        /** Number of bits per word */
        public static int cBitsLog2 = 5;

        /** 2^n Bits */
        public static int cWordBits = 32;

        /**
         * Constructor.
         */
        public BitVector() { }

        /**
         * Constructor.
         * @param bitVector BitVector
         */
        public BitVector(BitVector bitVector) : base(bitVector) { }

        /**
         * Sets the length of the vector (not implemented).
         * @param length Length of vector
         */
        public void setLength(int length) {
            for (int i = 0; i < length; i++) {
                Add(0L);
            }
        }

        /**
         * Test whether the bit at the given index is set.
         * @param  pos Index of the requested bit
         * @return     Is the bit set?
         */
        public bool test(int pos) {
            int vpos = pos >> cBitsLog2;
            if (vpos < Count) {
                long value1 = this[(pos >> cBitsLog2)];
                long value2 = (long)1 << (pos % cWordBits);
                bool result = (0 != (value1 & value2));
                return result;
            }
            else {
                return false;
            }
        }

        /**
         * Sets the bit at the given index.
         * @param pos Index of the bit to set
         */
        public void set(int pos) {
            int vpos = pos >> cBitsLog2;
            if (vpos >= Count) {
                while (Count < (vpos + 1)) {
                    base.Add(0L);
                }
            }

            long oldValue = this[vpos];
            long newValue = ((long)1 << ((pos % cWordBits)));
            this[vpos] = (oldValue | newValue);
        }
    }
}
