﻿using System;
using System.Threading.Tasks;
using Wenli.Live.RtmpLib.Interfaces;

namespace Wenli.Live.RtmpLib.Amfs.AMF0.AMFWriters
{
    class Amf0ArrayWriter : IAmfItemWriter
    {
        public void WriteData(AmfWriter writer, object obj)
        {
            writer.WriteMarker(Amf0TypeMarkers.StrictArray);
            writer.WriteAmf0Array(obj as Array);
        }

        public void WriteDataAsync(AmfWriter writer, object obj)
        {
            writer.WriteMarkerAsync(Amf0TypeMarkers.StrictArray);
            writer.WriteAmf0ArrayAsync(obj as Array);
        }
    }
}
