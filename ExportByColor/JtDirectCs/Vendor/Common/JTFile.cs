using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using JTReader;
using JTReader.Common;

namespace DLAT.JTReader {
    public class JTFile {
        public readonly string _filePath;
        string _version;
        public string Version {
            get {
                return _version;
            }
            set {
                var sp = value.Split(' ');
                _version = sp[1];
                majorVersion = int.Parse(_version.Split('.')[0]);
                minorVersion = int.Parse(_version.Split('.')[1]);
            }
        }
        public int majorVersion;
        public int minorVersion;
        public readonly int byteOrder;
        
        public byte[] fileBytes;
        public long tocOffset;
        
        public Dictionary<GUID, DataSegment> segments;
        public Dictionary<int, Element> elements;
        public GUID lsgSegmentID;
        public DataSegment LSGSegment;
        public PropertyTable propertyTable;

        static bool isXZInited = false;
        
        public int tocEntryLength = 0;

        public string txKinModelingBuffer = null;
        
        public JTFile(string filePath) {
            _filePath = filePath;
            var start = DateTime.Now;
            
            if(!isXZInited) 
                InitNativeLibrary();

            fileBytes = File.ReadAllBytes(filePath);
            long filePos = 0;
            Version = fileBytes.ReadString(ref filePos,80);

            byteOrder = fileBytes.ReadU8(ref filePos);
            if (byteOrder == 1)
                throw new Exception("This file saved as big endian. Doesn't support yet.");
            var emptyField = fileBytes.ReadI32(ref filePos);
            tocOffset = majorVersion > 9 ? fileBytes.ReadI64(ref filePos) : (long)fileBytes.ReadI32(ref filePos);
            tocEntryLength = majorVersion > 9 ? 32 : 28;
            lsgSegmentID = new GUID(fileBytes.ReadBytes(ref filePos, 16));

            filePos = tocOffset;
            var entryCount = fileBytes.ReadI32(ref filePos);
            
            //Debug.Log("Segment Count:" + entryCount);
            // var segTasks = new List<Task<DataSegment>>();
            // for (int i = 0; i < entryCount; ++i) {
            //     var iSeg = i;
            //     segTasks.Add(Task.Run(() => { return new DataSegment(this, iSeg); }));
            // }
            // Task.WaitAll(segTasks.ToArray());
            // segments = new List<DataSegment>();
            // foreach (var segTask in segTasks) {
            //     var seg = segTask.Result;
            //     //seg.InitializeElements();
            //     segments.Add(seg);
            // }
            //Console.WriteLine("v" + Version + " seg:" + entryCount + " " + filePath);

            elements = new Dictionary<int, Element>();
            segments = new Dictionary<GUID, DataSegment>();
            for (int i = 0; i < entryCount; ++i) {
                var seg = new DataSegment(this, i);
                if(seg.segmentID != null)
                    segments.Add(seg.segmentID, seg);
                if (seg.segmentID == lsgSegmentID)
                    LSGSegment = seg;
            }
            fileBytes = null;
            
            // foreach (var seg in segments) {
            //     if(seg.dataStream == null)
            //         throw new Exception("WTF?!");
            //     
            // }
            //Console.WriteLine("t:" + (DateTime.Now - start).TotalSeconds);
            //Debug.Log("#gRead Done!#w");
        }


        public static void InitNativeLibrary() {
            // LZMA decompression now uses SharpCompress (pure managed), so there is no native
            // liblzma library to initialize. Kept as a no-op for call-site compatibility.
            isXZInited = true;
        }

    }
}
