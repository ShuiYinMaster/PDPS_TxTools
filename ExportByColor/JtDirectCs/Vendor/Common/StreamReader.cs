using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DLAT.JTReader {
    public class StreamReader {
        private BinaryReader binReader;
        public readonly JTFile jtFile;

        public StreamReader(Stream stream, JTFile file) {
            stream.Position = 0;
            binReader = new BinaryReader(stream);
            jtFile = file;
        }

        public long Position {
            get => binReader.BaseStream.Position;
            set => binReader.BaseStream.Position = value;
        }
        public long Length => binReader.BaseStream.Length;
        public Stream BaseStream => binReader.BaseStream;
        
        public byte[] ReadBytes(int count, int padEnd = 0) {
            return binReader.ReadBytes(count);
        }
        public byte[] ReadBytes(long pos, int count, int padEnd = 0) {
            binReader.BaseStream.Position = pos;
            return ReadBytes(count);
        }
        public byte ReadU8() {
            return binReader.ReadByte();
        }
        public short ReadI16() {
            return binReader.ReadInt16();
        }
        public ushort ReadU16() {
            return binReader.ReadUInt16();
        }
        public int ReadI32() {
            return binReader.ReadInt32();
        }
        public uint ReadU32() {
            return binReader.ReadUInt32();
        }
        public long ReadI64() {
            return binReader.ReadInt64();
        }
        public ulong ReadU64() {
            return binReader.ReadUInt64();
        }
        public GUID ReadGUID() {
            return new GUID(binReader.ReadBytes(16));
        }
        public float ReadF32() {
            return binReader.ReadSingle();
        }
        public double ReadF64() {
            return binReader.ReadDouble();
        }
        public string ReadString() {
            return ReadString(ReadI32());
        }
        public string ReadString(int len) {
            var str = Encoding.UTF8.GetString(binReader.ReadBytes(len));
            //Debug.Log(str + Debug.StackTrace(1));
            return str;
        }
        public string ReadMbString() {
            var len = ReadI32();
            var bytes = binReader.ReadBytes(len * 2);
            var str = Encoding.Unicode.GetString(bytes);
            //Debug.Log(str + Debug.StackTrace(1));
            return str;
        }
        public RGBA ReadRGBA() {
            return new RGBA(this);
        }
        public List<int> ReadVecI32() {
            var count = binReader.ReadInt32();
            var res = new List<int>(count);
            for (int i = 0; i < count; ++i)
                res.Add(binReader.ReadInt32());
            return res;
        }
        public List<uint> ReadVecU32() {
            var count = binReader.ReadInt32();
            var res = new List<uint>(count);
            for (int i = 0; i < count; ++i)
                res.Add(binReader.ReadUInt32());
            return res;
        }
        public List<float> ReadVecF32() {
            var count = binReader.ReadInt32();
            var res = new List<float>(count);
            for (int i = 0; i < count; ++i)
                res.Add(binReader.ReadSingle());
            return res;
        }

        public byte ReadVersionNumber() {
            var jt = jtFile;
            return jt.majorVersion > 9 ? binReader.ReadByte() : (byte)binReader.ReadInt16();
        }
        
    }
}
