﻿using System.Threading.Tasks;
using System.Xml.Linq;
using Wenli.Live.RtmpLib.Interfaces;

namespace Wenli.Live.RtmpLib.Amfs.AMF0.AMFWriters
{
    class Amf0XDocumentWriter : IAmfItemWriter
    {
        public void WriteData(AmfWriter writer, object obj)
        {
            writer.WriteMarker(Amf0TypeMarkers.Xml);
            writer.WriteAmf0XDocument(obj as XDocument);
        }

        public void WriteDataAsync(AmfWriter writer, object obj)
        {
            writer.WriteMarkerAsync(Amf0TypeMarkers.Xml);
            writer.WriteAmf0XDocumentAsync(obj as XDocument);
        }
    }
}
