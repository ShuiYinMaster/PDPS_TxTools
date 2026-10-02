using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class BitStream {
        private long _offset;
        private long _position;
        private long _length;
        private StreamReader _byteStream;

        public JTFile fromJTFile => _byteStream.jtFile;

        public long Length => _length;
        public long Position => _position - _offset;

        public BitStream(StreamReader stream, int bitLength = -1) {
            _byteStream = stream;
            _offset = stream.Position << 3;
            _position = _offset;
            _length = bitLength == -1 ? (stream.Length - stream.Position) << 3 : bitLength;
        }

        public BitStream(long position) {
            _position = position;
        }

        public byte[] GetRequredBytes(int bitLnegth) {
            _byteStream.Position = _position / 8;
            var bytesLen = (_position + bitLnegth - 1) / 8 - _byteStream.Position + 1;
            var bytes = new byte[bytesLen];
            _ = _byteStream.BaseStream.Read(bytes, 0, bytes.Length);
            return bytes;
        }
        
        public int ReadU32(int bitLength) {
            if (bitLength < 1)
                return 0;
            var res = 0;
            var bytes = GetRequredBytes(bitLength);
            
            //read from high to low bit
            
            //chop left
            var left = (byte)(_position % 8);
            bytes[0] &= (byte)(0xff >> left);
            //chop right
            var right = (byte)((8 - (_position + bitLength) % 8)) % 8;
            bytes[bytes.Length - 1] &= (byte)(0xff << right);
            bytes[bytes.Length - 1] >>= right;
            
            res |= bytes[0];
            if (bytes.Length != 1) {
                for (var i = 1; i < bytes.Length - 1; i++) {
                    res <<= 8;
                    res |= bytes[i];
                }

                res <<= (8 - right);
                res |= bytes[bytes.Length - 1];
            }

            _position += bitLength;
            ApplyPositionToByteStream();
            return res;
        }
        public int ReadI32(int bitLength) {
            var res = ReadU32(bitLength);
            res <<= (32 - bitLength);
            res >>= (32 - bitLength);
            return res;
        }
        public float ReadF32(int bitLength) {
            var bytes = BitConverter.GetBytes(ReadI32(bitLength));
            return BitConverter.ToSingle(bytes, 0);
        }

        private void ApplyPositionToByteStream() {
            var pos = _position / 8 + (_position % 8 == 0 ? 0 : 1);
            _byteStream.Position = pos;
        }

    }
}
