using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DLAT.JTReader;

namespace JTReader.Common {
    public static class ArrayReader {
        public static byte[] ReadBytes(this byte[] data, ref long pos, int count, int padEnd = 0) {
            var buffer = new byte[count + padEnd];
            for(int i = 0; i < count; ++i)
                buffer[i] = data[pos + i];
            pos += count;
            return buffer;
        }
        public static byte ReadU8(this byte[] data,ref long pos) {
            return ReadBytes(data, ref pos, 1)[0];
        }
        public static short ReadI16(this byte[] data, ref long pos) {
            return BitConverter.ToInt16(ReadBytes(data, ref pos, 2), 0);
        }
        public static ushort ReadU16(this byte[] data, ref long pos) {
            return BitConverter.ToUInt16(ReadBytes(data, ref pos, 2), 0);
        }
        public static int ReadI32(this byte[] data, ref long pos) {
            return BitConverter.ToInt32(ReadBytes(data, ref pos, 4), 0);
        }
        public static uint ReadU32(this byte[] data, ref long pos) {
            return BitConverter.ToUInt32(ReadBytes(data, ref pos, 4), 0);
        }
        public static long ReadI64(this byte[] data, ref long pos) {
            return BitConverter.ToInt64(ReadBytes(data, ref pos, 8), 0);
        }
        public static ulong ReadU64(this byte[] data, ref long pos) {
            return BitConverter.ToUInt64(ReadBytes(data, ref pos, 8), 0);
        }
        public static GUID ReadGUID(this byte[] data, ref long pos) {
            return new GUID(ReadBytes(data, ref pos, 16));
        }
        public static float ReadF32(this byte[] data, ref long pos) {
            return BitConverter.ToSingle(ReadBytes(data, ref pos, 4), 0);
        }
        public static double ReadF64(this byte[] data, ref long pos) {
            return BitConverter.ToDouble(ReadBytes(data, ref pos, 8), 0);
        }
        public static string ReadString(this byte[] data, ref long pos) {
            return ReadString(data, ref pos,data.ReadI32(ref pos));
        }
        public static string ReadString(this byte[] data, ref long pos, int len) {
            return Encoding.UTF8.GetString(ReadBytes(data, ref pos, len));
        }
        public static string ReadMbString(this byte[] data, ref long pos) {
            var len = data.ReadI32(ref pos);
            return Encoding.Unicode.GetString(ReadBytes(data,ref pos, len * 2));
        }
        public static RGBA ReadRGBA(this byte[] data,ref long pos) {
            return new RGBA(BitConverter.ToSingle(data,(int)(pos + 0)),
                BitConverter.ToSingle(data,(int)(pos + 4)),
                BitConverter.ToSingle(data,(int)(pos + 8)),
                BitConverter.ToSingle(data,(int)(pos + 12)));
        }
        public static List<int> ReadVecI32(this byte[] data, ref long pos) {
            var count = data.ReadI32(ref pos);
            var res = new List<int>(count);
            for (int i = 0; i < count; ++i)
                res.Add(data.ReadI32(ref pos));
            return res;
        }
        public static List<uint> ReadVecU32(this byte[] data, ref long pos) {
            var count = data.ReadI32(ref pos);
            var res = new List<uint>(count);
            for (int i = 0; i < count; ++i)
                res.Add(data.ReadU32(ref pos));
            return res;
        }
        public static List<float> ReadVecF32(this byte[] data, ref long pos) {
            var count = data.ReadI32(ref pos);
            var res = new List<float>(count);
            for (int i = 0; i < count; ++i)
                res.Add(data.ReadF32(ref pos));
            return res;
        }
    }
}