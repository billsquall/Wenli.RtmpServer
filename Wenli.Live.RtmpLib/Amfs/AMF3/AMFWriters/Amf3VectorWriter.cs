﻿using System;
using System.Collections;
using System.Threading.Tasks;
using Wenli.Live.RtmpLib.Interfaces;

namespace Wenli.Live.RtmpLib.Amfs.AMF3.AMFWriters
{
    class Amf3VectorWriter<T> : IAmfItemWriter
    {
        readonly Amf3TypeMarkers typeMarker;
        readonly Action<AmfWriter, IList> write;

        public Amf3VectorWriter(Amf3TypeMarkers typeMarker, Action<AmfWriter, IList> write)
        {
            this.typeMarker = typeMarker;
            this.write = write;
        }

        public void WriteData(AmfWriter writer, object obj)
        {
            writer.WriteMarker(this.typeMarker);
            write(writer, obj as IList);
        }

        public void WriteDataAsync(AmfWriter writer, object obj)
        {
            writer.WriteMarkerAsync(this.typeMarker);
            write(writer, obj as IList);
        }
    }
}