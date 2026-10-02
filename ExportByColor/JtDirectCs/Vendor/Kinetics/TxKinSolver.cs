namespace DLAT.JTReader {
    public class TxKinSolver {
        public TxKinDevices devices;
        public TxKinSolver(string txKinModelingBuffer) {
            if (txKinModelingBuffer == null)
                return;
            var sp = txKinModelingBuffer.Split(',');
            if (sp.Length < 3 || sp[2].Length < 2)
                return;
            var xml = sp[2].Remove(sp[2].Length - 2, 2);
            devices = TxKinDevices.Create(xml);
        }
        
        void Init() {
            
        }
        
    }
}