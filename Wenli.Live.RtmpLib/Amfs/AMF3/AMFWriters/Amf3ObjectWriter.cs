﻿using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wenli.Live.RtmpLib.Interfaces;

namespace Wenli.Live.RtmpLib.Amfs.AMF3.AMFWriters
{
    class Amf3ObjectWriter : IAmfItemWriter
    {
        public void WriteData(AmfWriter writer, object obj)
        {
            var externalizable = obj is IExternalizable;

            // if IExternalizable then use those methods, even if it is a collection
            if (!externalizable)
            {
                IDictionary<string, object> stringDictionary;
                IDictionary dictionary;
                IEnumerable enumerable;

                if ((stringDictionary = obj as IDictionary<string, object>) != null)
                {
                    writer.WriteMarker(Amf3TypeMarkers.Array);
                    writer.WriteAmf3AssociativeArray(stringDictionary);
                    return;
                }
                if ((dictionary = obj as IDictionary) != null)
                {
                    writer.WriteMarker(Amf3TypeMarkers.Dictionary);
                    writer.WriteAmf3Dictionary(dictionary);
                }
                if ((enumerable = obj as IEnumerable) != null)
                {
                    writer.WriteMarker(Amf3TypeMarkers.Array);
                    writer.WriteAmf3Array(enumerable.Cast<object>().ToArray());
                    return;
                }
            }

            writer.WriteMarker(Amf3TypeMarkers.Object);
            writer.WriteAmf3Object(obj);
        }

        public void WriteDataAsync(AmfWriter writer, object obj)
        {
            var externalizable = obj is IExternalizable;

            // if IExternalizable then use those methods, even if it is a collection
            if (!externalizable)
            {
                IDictionary<string, object> stringDictionary;
                IDictionary dictionary;
                IEnumerable enumerable;

                if ((stringDictionary = obj as IDictionary<string, object>) != null)
                {
                    writer.WriteMarkerAsync(Amf3TypeMarkers.Array);
                    writer.WriteAmf3AssociativeArrayAsync(stringDictionary);
                    return;
                }
                if ((dictionary = obj as IDictionary) != null)
                {
                    writer.WriteMarkerAsync(Amf3TypeMarkers.Dictionary);
                    writer.WriteAmf3DictionaryAsync(dictionary);
                }
                if ((enumerable = obj as IEnumerable) != null)
                {
                    writer.WriteMarkerAsync(Amf3TypeMarkers.Array);
                    writer.WriteAmf3ArrayAsync(enumerable.Cast<object>().ToArray());
                    return;
                }
            }

            writer.WriteMarkerAsync(Amf3TypeMarkers.Object);
            writer.WriteAmf3ObjectAsync(obj);
        }
    }
}