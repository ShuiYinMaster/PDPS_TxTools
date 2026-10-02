using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using System.IO.Compression;
using System.Runtime.InteropServices.ComTypes;
using System.Diagnostics;
using JTReader;
using JTReader.Common;

namespace DLAT.JTReader {
    public class DataSegment {
        public JTFile file;
        public GUID segmentID;
        public int segmentType;
        public uint segmentLength;
        public StreamReader dataStream;

        public List<Element> elements;

        public int majorVersion {
            get {
                return file.majorVersion;
            }
        }
        public int minorVersion {
            get {
                return file.minorVersion;
            }
        }
        
        public DataSegment(JTFile jtFile, int segIndex) {
            file = jtFile;
            var fb = file.fileBytes;
            var pos = 4 + jtFile.tocOffset + segIndex * jtFile.tocEntryLength;
            //----TOC Entry----
            segmentID = fb.ReadGUID(ref pos);
            var segmentOffset = majorVersion > 9 ? fb.ReadI64(ref pos) : fb.ReadI32(ref pos);
            segmentLength = fb.ReadU32(ref pos);
            var segmentAttribute = fb.ReadBytes(ref pos, 4);
            segmentType = segmentAttribute[3];
            //----TOC Entry----

            //----Segment Header----
            pos = segmentOffset;
            if (segmentID != fb.ReadGUID(ref pos))
                throw new Exception("TOC Entry Segment ID doesn't equal to Segment Header Segment ID");
            if (segmentType != fb.ReadI32(ref pos))
                throw new Exception("TOC Entry Segment Type doesn't equal to Segment Header Segment Type");
            if (segmentLength != fb.ReadI32(ref pos))
                throw new Exception("TOC Entry Segment Length doesn't equal to Segment Header Segment Length");
            //----Segment Header----

            if (segmentType == 0) {
                //Debug.Log("#rFound segment type 0, skipped.#w");
                return;
            }
            
            var compressed = false;
            var supportCompress = SegmentTypes.GetType(segmentType).Item2;
            if(supportCompress) {
                var compressionFlag = fb.ReadI32(ref pos);
                var compressedLength = fb.ReadI32(ref pos) - 1;
                var compressionAlgorithm = fb.ReadU8(ref pos);

                //Console.WriteLine(file.version + " " + compressionAlgorithm);
                if (compressionFlag == 2 && compressionAlgorithm == 2) {
                    compressed = true;
                    pos += 2; //skip zlib header
                    dataStream = new StreamReader(
                        CODEC.DecompressZLIB(new MemoryStream(fb, (int)pos, compressedLength - 2)),
                        file);
                } else if (compressionFlag == 3 && compressionAlgorithm == 3) {
                    compressed = true;
                    dataStream = new StreamReader(
                        CODEC.DecompressLZMA(new MemoryStream(fb, (int)pos, compressedLength)),
                        file);
                } else
                    throw new Exception("unknown compress method");
            }
            if(!compressed) 
                dataStream = new StreamReader(new MemoryStream(fb, (int)pos,(int)segmentLength - 24),
                    file);
            
            elements = new List<Element>();
            bool graphElementsRead = (file.majorVersion >= 10 && file.minorVersion >= 5);
            for(;dataStream.Position < dataStream.Length ; ) {
                var ele = new Element(this);
                if (ele.objectTypeID.isEOE()) {
                    if (segmentID != file.lsgSegmentID || graphElementsRead)
                        break;
                    graphElementsRead = true;
                }
                elements.Add(ele);
                if (ele.objectID != -1 && !file.elements.ContainsKey(ele.objectID))
                    file.elements.Add(ele.objectID, ele);
            }

            if (segmentID == file.lsgSegmentID)
                file.propertyTable = new PropertyTable(dataStream);
            
            dataStream = null;

            //Debug.Log("Segment type(#b" +
            //          SegmentTypes.GetType(segmentType).Item1 + "#w) ID:" + segmentID + " begin:" + fs.Position +
            //          " len:" +
            //          segmentLength + " end:" + (fs.Position + segmentLength) + "| dataLen:" + dataStream.Length);
        }

        
    }
}
