﻿using System;
using System.Threading.Tasks;
using Wenli.Live.RtmpLib.Interfaces;

namespace Wenli.Live.RtmpLib.Amfs.AMF0.AMFWriters
{
    class Amf0EnumWriter : IAmfItemWriter
    {
        public void WriteData(AmfWriter writer, object obj)
        {
            writer.WriteMarker(Amf0TypeMarkers.Number);
            writer.WriteDouble(Convert.ToDouble(obj));
        }

        public void WriteDataAsync(AmfWriter writer, object obj)
        {
            writer.WriteMarkerAsync(Amf0TypeMarkers.Number);
            writer.WriteDoubleAsync(Convert.ToDouble(obj));
        }
    }
}
