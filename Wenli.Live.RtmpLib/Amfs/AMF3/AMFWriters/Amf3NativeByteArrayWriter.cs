﻿
using System.Threading.Tasks;
using Wenli.Live.RtmpLib.Interfaces;

namespace Wenli.Live.RtmpLib.Amfs.AMF3.AMFWriters
{
    class Amf3NativeByteArrayWriter : IAmfItemWriter
    {
        public void WriteData(AmfWriter writer, object obj)
        {
            writer.WriteMarker(Amf3TypeMarkers.ByteArray);
            // We're just writing a plain byte array, so we don't need a serialization context
            writer.WriteAmf3ByteArray(new ByteArray((byte[])obj, null));
        }

        public void WriteDataAsync(AmfWriter writer, object obj)
        {
            writer.WriteMarkerAsync(Amf3TypeMarkers.ByteArray);
            // We're just writing a plain byte array, so we don't need a serialization context
            writer.WriteAmf3ByteArrayAsync(new ByteArray((byte[])obj, null));
        }
    }
}