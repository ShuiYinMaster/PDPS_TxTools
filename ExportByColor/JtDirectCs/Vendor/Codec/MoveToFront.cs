using System.Collections.Generic;

namespace DLAT.JTReader {

    public class RecentlyUsedList {
        private LinkedList<int> window;

        private int _length;
        public RecentlyUsedList(int length) {
            _length = length;
            window = new LinkedList<int>();
        }
        
        public int Count => window.Count;

        public int this[int index] {
            get {
                if (index >= window.Count)
                    throw new System.IndexOutOfRangeException();
                var node = window.First;
                for (var i = 0; i < index; ++i)
                    node = node.Next;
                var res = node.Value;
                window.Remove(node);
                window.AddFirst(res);
                return res;
            }
        }

        public void Add(int value) {
            window.AddFirst(value);
            if(window.Count > _length)
                window.RemoveLast();
        }
    }
    
    public static class MoveToFront {
        public static int[] Decode(int[] values, int[] offsets) {
            var window = new RecentlyUsedList(16);
            var decodedSymbols = new List<int>();
            var valueIndex = 0;
            foreach (var offset in offsets) {
                if (offset == -1) {
                    var val = values[valueIndex++];
                    window.Add(val);
                    decodedSymbols.Add(val);
                }
                else {
                    decodedSymbols.Add(window[offset]);
                }
            }
            return decodedSymbols.ToArray();
        }
    }
}