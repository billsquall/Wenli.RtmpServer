﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Wenli.Live.Common;
using Wenli.Live.RtmpLib.Amfs.AMF0;
using Wenli.Live.RtmpLib.Amfs.AMF0.AMFWriters;
using Wenli.Live.RtmpLib.Amfs.AMF3;
using Wenli.Live.RtmpLib.Amfs.AMF3.AMFWriters;
using Wenli.Live.RtmpLib.Interfaces;
using Wenli.Live.RtmpLib.Libs;
using Wenli.Live.RtmpLib.Models;

namespace Wenli.Live.RtmpLib.Amfs
{
    public class AmfWriter : IDisposable
    {
        // [0, 2^29-1]
        static readonly int[] UInt29Range = new[] { 0, 536870911 };
        // [-2^28, 2^28-1]
        static readonly int[] Int29Range = new[] { -268435456, 268435455 };
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);

        static readonly AmfWriterMap Amf0Writers;
        static readonly AmfWriterMap Amf3Writers;

        public SerializationContext SerializationContext { get; private set; }

        readonly BinaryWriter underlying;
        readonly ObjectEncoding objectEncoding;
        readonly Dictionary<object, int> amf0ObjectReferences;
        readonly Dictionary<object, int> amf3ObjectReferences;
        readonly Dictionary<object, int> amf3StringReferences;
        readonly Dictionary<ClassDescription, int> amf3ClassDefinitionReferences;
        private bool asyncMode;
        private Stream asyncBaseStream;
        private MemoryStream asyncBuffer;

        static AmfWriter()
        {
            var smallIntTypes = new[]
            {
                typeof(SByte),
                typeof(Byte),
                typeof(Int16),
                typeof(UInt16),
                typeof(Int32),
                typeof(UInt32)
            };

            var bigOrFloatingTypes = new[]
            {
                typeof(Int64),
                typeof(UInt64),
                typeof(Single),
                typeof(Double),
                typeof(Decimal)
            };

            Amf0Writers = new AmfWriterMap(new Amf0ObjectWriter())
            {
                { typeof(Array),        new Amf0ArrayWriter() },
                { typeof(AsObject),     new Amf0AsObjectWriter() },
                { typeof(bool),         new Amf0BooleanWriter() },
                { typeof(char),         new Amf0CharWriter() },
                { typeof(DateTime),     new Amf0DateTimeWriter() },
                { typeof(Enum),         new Amf0EnumWriter() },
                { typeof(Guid),         new Amf0GuidWriter() },
                { typeof(string),       new Amf0StringWriter() },
                { typeof(XDocument),    new Amf0XDocumentWriter() },
                { typeof(XElement),     new Amf0XElementWriter() },
            };

            var amf0NumberWriter = new Amf0NumberWriter();
            foreach (var type in smallIntTypes.Concat(bigOrFloatingTypes))
                Amf0Writers.Add(type, amf0NumberWriter);

            Amf3Writers = new AmfWriterMap(new Amf3ObjectWriter())
            {
                { typeof(Array),        new Amf3ArrayWriter() },
                { typeof(AsObject),     new Amf3AsObjectWriter() },
                { typeof(bool),         new Amf3BooleanWriter() },
                { typeof(ByteArray),    new Amf3ByteArrayWriter() },
                { typeof(char),         new Amf3CharWriter() },
                { typeof(DateTime),     new Amf3DateTimeWriter() },
                { typeof(Enum),         new Amf3EnumWriter() },
                { typeof(Guid),         new Amf3GuidWriter() },
                { typeof(string),       new Amf3StringWriter() },
                { typeof(XDocument),    new Amf3XDocumentWriter() },
                { typeof(XElement),     new Amf3XElementWriter() },
                { typeof(byte[]),       new Amf3NativeByteArrayWriter() },

                // `IDictionary`s are handled in the object writer
            };

            var amf3IntWriter = new Amf3IntWriter();
            foreach (var type in smallIntTypes)
                Amf3Writers.Add(type, amf3IntWriter);

            var amf3FloatingWriter = new Amf3DoubleWriter();
            foreach (var type in bigOrFloatingTypes)
                Amf3Writers.Add(type, amf3FloatingWriter);
        }

        // add write support for the new specialized vector and dictionary types introduced into the AMF3 specification by Adobe for Flash 10
        // old servers do not understand this, so it is an optional call.
        public static void EnableFlash10Writers()
        {
            var createVectorIntWriter = new Func<bool, IAmfItemWriter>(isFixed => new Amf3VectorWriter<int>(Amf3TypeMarkers.VectorInt, (writer, list) => writer.WriteAmf3Vector<int>(false, isFixed, list, writer.WriteInt32)));
            var createVectorUIntWriter = new Func<bool, IAmfItemWriter>(isFixed => new Amf3VectorWriter<uint>(Amf3TypeMarkers.VectorInt, (writer, list) => writer.WriteAmf3Vector<uint>(false, isFixed, list, i => writer.WriteInt32((int)i))));
            var createVectorDoubleWriter = new Func<bool, IAmfItemWriter>(isFixed => new Amf3VectorWriter<double>(Amf3TypeMarkers.VectorInt, (writer, list) => writer.WriteAmf3Vector<double>(false, isFixed, list, writer.WriteDouble)));
            var createVectorObjectWriter = new Func<bool, IAmfItemWriter>(isFixed => new Amf3VectorWriter<object>(Amf3TypeMarkers.VectorInt, (writer, list) => writer.WriteAmf3Vector<object>(true, isFixed, list, writer.WriteAmf3Item)));
            var amf3Flash10Writers = new Dictionary<Type, IAmfItemWriter>
            {
                { typeof(int[]),         createVectorIntWriter(true) },
                { typeof(List<int>),     createVectorIntWriter(false) },
                { typeof(uint[]),        createVectorUIntWriter(true) },
                { typeof(List<uint>),    createVectorUIntWriter(false) },
                { typeof(double[]),      createVectorDoubleWriter(true) },
                { typeof(List<double>),  createVectorDoubleWriter(false) },
                { typeof(object[]),      createVectorObjectWriter(true) },
                { typeof(List<object>),  createVectorObjectWriter(false) },
            };

            foreach (var pair in amf3Flash10Writers)
                Amf3Writers[pair.Key] = pair.Value;
        }

        public AmfWriter(Stream stream, SerializationContext serializationContext) : this(stream, serializationContext, ObjectEncoding.Amf3)
        {
        }

        public AmfWriter(Stream stream, SerializationContext serializationContext, ObjectEncoding objectEncoding, bool asyncMode = false)
        {
            this.asyncMode = asyncMode;
            if (asyncMode)
            {
                asyncBuffer = new MemoryStream();
                asyncBaseStream = stream;
            }
            else
            {
                underlying = new BinaryWriter(stream);
            }
            this.objectEncoding = objectEncoding;
            amf0ObjectReferences = new Dictionary<object, int>();
            amf3ObjectReferences = new Dictionary<object, int>();
            amf3StringReferences = new Dictionary<object, int>();
            amf3ClassDefinitionReferences = new Dictionary<ClassDescription, int>();

            SerializationContext = serializationContext;
        }

        public void Dispose()
        {
            underlying?.Dispose();
        }

        #region helpers

        public long Length => underlying.BaseStream.Length;
        public long Position => underlying.BaseStream.Position;
        public bool DataAvailable => Position < Length;

        static IAmfItemWriter GetAmfWriter(AmfWriterMap writerMap, Type type)
        {
            IAmfItemWriter amfWriter;

            // use the writer specified within our dictionary, if it exists.
            if (writerMap.TryGetValue(type, out amfWriter))
                return amfWriter;

            // try the lookup again but with the base type (so we can serialize enums and arrays,
            // for example).
            if (type.BaseType != null && writerMap.TryGetValue(type.BaseType, out amfWriter))
                return amfWriter;

            // no writer exists. Create and cache the default one so we don't need to go through this
            // expensive lookup again.
            lock (writerMap)
            {
                // check inside lock since type may have been added since our initial check
                if (writerMap.TryGetValue(type, out amfWriter))
                    return amfWriter;

                amfWriter = writerMap.DefaultWriter;
                writerMap.Add(type, amfWriter);
                return amfWriter;
            }
        }

        public void Reset()
        {
            amf0ObjectReferences.Clear();
            amf3ObjectReferences.Clear();
            amf3StringReferences.Clear();
            amf3ClassDefinitionReferences.Clear();
        }

        public long Seek(int offset, SeekOrigin origin)
        {
            return underlying.Seek(offset, origin);
        }

        public void Flush()
        {
            underlying.Flush();
        }

        public void Write(byte value)
        {
            if (asyncMode) throw new InvalidOperationException("can only work on sync mode");
            underlying.Write(value);
        }

        public void WriteAsync(byte value)
        {
            if (!asyncMode) throw new InvalidOperationException("can only work on async mode");
            WriteAsync(new byte[] { value }, 0, 1);
        }

        public void Write(byte[] value)
        {
            if (asyncMode) throw new InvalidOperationException("can only work on sync mode");
            underlying.Write(value);
        }

        public void WriteAsync(byte[] value)
        {
            if (!asyncMode) throw new InvalidOperationException("can only work on async mode");
            WriteAsync(value, 0, value.Length);
        }

        public void Write(byte[] bytes, int index, int count)
        {
            if (asyncMode) throw new InvalidOperationException("can only work on sync mode");
            underlying.Write(bytes, index, count);
        }


        public async Task StartWriteAsync()
        {
            if (!asyncMode) throw new InvalidOperationException("can only work on async mode");
            await asyncBaseStream.WriteAsync(asyncBuffer.GetBuffer(), 0, (int)asyncBuffer.Length);
            asyncBuffer.SetLength(0);
            asyncBuffer.Seek(0, SeekOrigin.Begin);
        }

        public void WriteAsync(byte[] bytes, int index, int count)
        {
            if (!asyncMode) throw new InvalidOperationException("can only work on async mode");
            asyncBuffer.Write(bytes, index, count);
        }

        public void WriteByte(byte value)
        {
            Write(value);
        }

        public void WriteByteAsync(byte value)
        {
            WriteAsync(value);
        }

        public void WriteBytes(byte[] buffer)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            Write(buffer);
        }

        public void WriteBytesAsync(byte[] buffer)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            WriteAsync(buffer);
        }


        internal void WriteMarker(Amf0TypeMarkers marker)
        {
            Write((byte)marker);
        }

        internal void WriteMarkerAsync(Amf0TypeMarkers marker)
        {
            WriteAsync((byte)marker);
        }

        internal void WriteMarker(Amf3TypeMarkers marker)
        {
            Write((byte)marker);
        }

        internal void WriteMarkerAsync(Amf3TypeMarkers marker)
        {
            WriteAsync((byte)marker);
        }

        public void WriteInt16(short value)
        {
            var bytes = BitConverter.GetBytes(value);
            WriteBigEndian(bytes);
        }

        public void WriteInt16Async(short value)
        {
            var bytes = BitConverter.GetBytes(value);
            WriteBigEndianAsync(bytes);
        }

        public void WriteUInt16(ushort value)
        {
            var bytes = BitConverter.GetBytes(value);
            WriteBigEndian(bytes);
        }

        public void WriteUInt16Async(ushort value)
        {
            var bytes = BitConverter.GetBytes(value);
            WriteBigEndianAsync(bytes);
        }

        public void WriteDouble(double value)
        {
            var bytes = BitConverter.GetBytes(value);
            WriteBigEndian(bytes);
        }

        public void WriteDoubleAsync(double value)
        {
            var bytes = BitConverter.GetBytes(value);
            WriteBigEndianAsync(bytes);
        }

        public void WriteFloat(float value)
        {
            var bytes = BitConverter.GetBytes(value);
            WriteBigEndian(bytes);
        }

        public void WriteFloatAsync(float value)
        {
            var bytes = BitConverter.GetBytes(value);
            WriteBigEndianAsync(bytes);
        }

        public void WriteInt32(int value)
        {
            var bytes = BitConverter.GetBytes(value);
            WriteBigEndian(bytes);
        }

        public void WriteInt32Async(int value)
        {
            var bytes = BitConverter.GetBytes(value);
            WriteBigEndianAsync(bytes);
        }

        public void WriteUInt32(uint value)
        {
            var bytes = BitConverter.GetBytes(value);
            WriteBigEndian(bytes);
        }

        public void WriteUInt32Async(uint value)
        {
            var bytes = BitConverter.GetBytes(value);
            WriteBigEndianAsync(bytes);
        }

        public void WriteReverseInt(int value)
        {
            var bytes = new byte[4];
            bytes[3] = (byte)(0xFF & (value >> 24));
            bytes[2] = (byte)(0xFF & (value >> 16));
            bytes[1] = (byte)(0xFF & (value >> 8));
            bytes[0] = (byte)(0xFF & value);
            Write(bytes, 0, bytes.Length);
        }

        public void WriteReverseIntAsync(int value)
        {
            var bytes = new byte[4];
            bytes[3] = (byte)(0xFF & (value >> 24));
            bytes[2] = (byte)(0xFF & (value >> 16));
            bytes[1] = (byte)(0xFF & (value >> 8));
            bytes[0] = (byte)(0xFF & value);
            WriteAsync(bytes, 0, bytes.Length);
        }

        // writes a 32-bit signed integer to the current position in the AMF stream using variable length unsigned 29-bit integer encoding.
        public void WriteUInt24(int value)
        {
            if (value < UInt29Range[0] || value > UInt29Range[1])
                throw new ArgumentOutOfRangeException(nameof(value));

            var bytes = new byte[3];
            bytes[0] = (byte)(0xFF & (value >> 16));
            bytes[1] = (byte)(0xFF & (value >> 8));
            bytes[2] = (byte)(0xFF & (value >> 0));
            WriteBytes(bytes);
        }

        public void WriteUInt24Async(int value)
        {
            if (value < UInt29Range[0] || value > UInt29Range[1])
                throw new ArgumentOutOfRangeException(nameof(value));

            var bytes = new byte[3];
            bytes[0] = (byte)(0xFF & (value >> 16));
            bytes[1] = (byte)(0xFF & (value >> 8));
            bytes[2] = (byte)(0xFF & (value >> 0));
            WriteBytesAsync(bytes);
        }

        public void WriteBoolean(bool value)
        {
            Write(value ? (byte)1 : (byte)0);
        }

        public void WriteBooleanAsync(bool value)
        {
            WriteAsync(value ? (byte)1 : (byte)0);
        }

        // string with 16-bit length prefix
        internal void WriteUtfPrefixed(string str)
        {
            if (str == null)
                throw new ArgumentNullException(nameof(str));

            var bytes = Encoding.UTF8.GetBytes(str);
            WriteUtfPrefixed(bytes);
        }

        internal void WriteUtfPrefixedAsync(string str)
        {
            if (str == null)
                throw new ArgumentNullException(nameof(str));

            var bytes = Encoding.UTF8.GetBytes(str);
            WriteUtfPrefixedAsync(bytes);
        }

        void WriteUtfPrefixed(byte[] buffer)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            if (buffer.Length > ushort.MaxValue)
                throw new SerializationException("string is larger than maximum encodable value.");

            WriteUInt16((ushort)buffer.Length);
            Write(buffer);
        }

        void WriteUtfPrefixedAsync(byte[] buffer)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            if (buffer.Length > ushort.MaxValue)
                throw new SerializationException("string is larger than maximum encodable value.");

            WriteUInt16Async((ushort)buffer.Length);
            WriteAsync(buffer);
        }

        void WriteBigEndian(byte[] bytes)
        {
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            Write(bytes);
        }

        void WriteBigEndianAsync(byte[] bytes)
        {
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            WriteAsync(bytes);
        }

        #endregion


        #region Both

        // writes an object, starting in AMF0 encoding. If the AmfWriter's `encoding` is 3,
        // then an AMF3 marker will be written and encoding will be upgraded to AMF3. otherwise,
        // encoding will stay in AMF0. this method writes the type marker and string.
        public void WriteAmfItem(object data)
        {
            WriteAmfItem(objectEncoding, data);
        }

        public void WriteAmfItemAsync(object data)
        {
            WriteAmfItemAsync(objectEncoding, data);
        }


        // this method is required because of the functionality specified by classes like `IDataOutput`.
        public void WriteAmfItem(ObjectEncoding encoding, object data)
        {
            // if it's null, we don't need to do expensive operations to determine how to write it
            if (data == null)
            {
                WriteMarker(Amf0TypeMarkers.Null);
                return;
            }

            if (WriteAmf0ReferenceOnExistence(data))
                return;

            var type = data.GetType();

            switch (encoding)
            {
                case ObjectEncoding.Amf0:
                    var writer = GetAmfWriter(Amf0Writers, type);
                    writer.WriteData(this, data);
                    break;
                case ObjectEncoding.Amf3:
                    WriteMarker(Amf0TypeMarkers.Amf3Object);
                    WriteAmf3Item(data);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(encoding));
            }
        }

        public void WriteAmfItemAsync(ObjectEncoding encoding, object data)
        {
            // if it's null, we don't need to do expensive operations to determine how to write it
            if (data == null)
            {
                WriteMarkerAsync(Amf0TypeMarkers.Null);
                return;
            }

            if (WriteAmf0ReferenceOnExistence(data))
                return;

            var type = data.GetType();

            switch (encoding)
            {
                case ObjectEncoding.Amf0:
                    var writer = GetAmfWriter(Amf0Writers, type);
                    writer.WriteDataAsync(this, data);
                    break;
                case ObjectEncoding.Amf3:
                    WriteMarkerAsync(Amf0TypeMarkers.Amf3Object);
                    WriteAmf3Item(data);
                    // TODO
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(encoding));
            }
        }

        #endregion

        #region amf0

        internal void AddAmf0Reference(object value)
        {
            amf0ObjectReferences.Add(value, amf0ObjectReferences.Count);
        }

        // this method writes the type marker and string
        internal bool WriteAmf0ReferenceOnExistence(object value)
        {
            int index;
            if (!amf0ObjectReferences.TryGetValue(value, out index))
                return false;

            WriteMarker(Amf0TypeMarkers.Reference);
            WriteUInt16((ushort)amf0ObjectReferences[value]);
            return true;
        }

        internal bool WriteAmf0ReferenceOnExistenceAsync(object value)
        {
            int index;
            if (!amf0ObjectReferences.TryGetValue(value, out index))
                return false;

            WriteMarkerAsync(Amf0TypeMarkers.Reference);
            WriteUInt16Async((ushort)amf0ObjectReferences[value]);
            return true;
        }

        // this method writes the type marker and string
        internal void WriteAmf0StringSpecial(string str)
        {
            if (str == null)
                throw new ArgumentNullException(nameof(str));

            var bytes = Encoding.UTF8.GetBytes(str);
            var length = bytes.Length;

            if (length < ushort.MaxValue)
            {
                WriteMarker(Amf0TypeMarkers.String);
                WriteUtfPrefixed(bytes);
            }
            else
            {
                WriteMarker(Amf0TypeMarkers.LongString);
                WriteAmf0UtfLong(bytes);
            }
        }

        internal void WriteAmf0StringSpecialAsync(string str)
        {
            if (str == null)
                throw new ArgumentNullException(nameof(str));

            var bytes = Encoding.UTF8.GetBytes(str);
            var length = bytes.Length;

            if (length < ushort.MaxValue)
            {
                WriteMarkerAsync(Amf0TypeMarkers.String);
                WriteUtfPrefixedAsync(bytes);
            }
            else
            {
                WriteMarkerAsync(Amf0TypeMarkers.LongString);
                WriteAmf0UtfLongAsync(bytes);
            }
        }

        internal void WriteAmf0UtfLong(string value)
        {
            WriteAmf0UtfLong(Encoding.UTF8.GetBytes(value));
        }

        internal void WriteAmf0UtfLongAsync(string value)
        {
            WriteAmf0UtfLongAsync(Encoding.UTF8.GetBytes(value));
        }


        void WriteAmf0UtfLong(byte[] buffer)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            // length written as 32-bit uint
            WriteUInt32((uint)buffer.Length);
            WriteBytes(buffer);
        }

        void WriteAmf0UtfLongAsync(byte[] buffer)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            // length written as 32-bit uint
            WriteUInt32Async((uint)buffer.Length);
            WriteBytesAsync(buffer);
        }

        // this method writes the type marker and string
        public void WriteAmf0Item(object data)
        {
            // if it's null, we don't need to do expensive operations to determine how to write it
            if (data == null)
            {
                WriteMarker(Amf0TypeMarkers.Null);
                return;
            }


            if (WriteAmf0ReferenceOnExistence(data))
                return;
            var type = data.GetType();

            GetAmfWriter(Amf0Writers, type).WriteData(this, data);
        }

        public void WriteAmf0ItemAsync(object data)
        {
            // if it's null, we don't need to do expensive operations to determine how to write it
            if (data == null)
            {
                WriteMarkerAsync(Amf0TypeMarkers.Null);
                return;
            }


            if (WriteAmf0ReferenceOnExistenceAsync(data))
                return;
            var type = data.GetType();

            var writer = GetAmfWriter(Amf0Writers, type);
            writer.WriteDataAsync(this, data);
        }

        // this method writes the type marker and string
        internal void WriteAmf0AsObject(AsObject obj)
        {
            if (obj == null)
                throw new ArgumentNullException(nameof(obj));

            AddAmf0Reference(obj);
            var anonymousObject = string.IsNullOrEmpty(obj.TypeName);
            WriteMarker(anonymousObject ? Amf0TypeMarkers.Object : Amf0TypeMarkers.TypedObject);
            if (!anonymousObject)
                WriteUtfPrefixed(obj.TypeName);

            foreach (var property in obj)
            {
                WriteUtfPrefixed(property.Key);
                WriteAmf0Item(property.Value);
            }

            // end of object denoted by zero-length field name, then end of object type marker
            // field names are length-prefixed utf8 strings, so [0 length string, end of object type marker]
            WriteUInt16(0);
            WriteMarker(Amf0TypeMarkers.ObjectEnd);
        }

        internal void WriteAmf0AsObjectAsync(AsObject obj)
        {
            if (obj == null)
                throw new ArgumentNullException(nameof(obj));

            AddAmf0Reference(obj);
            var anonymousObject = string.IsNullOrEmpty(obj.TypeName);
            WriteMarkerAsync(anonymousObject ? Amf0TypeMarkers.Object : Amf0TypeMarkers.TypedObject);
            if (!anonymousObject)
                WriteUtfPrefixedAsync(obj.TypeName);

            foreach (var property in obj)
            {
                WriteUtfPrefixedAsync(property.Key);
                WriteAmf0ItemAsync(property.Value);
            }

            // end of object denoted by zero-length field name, then end of object type marker
            // field names are length-prefixed utf8 strings, so [0 length string, end of object type marker]
            WriteUInt16Async(0);
            WriteMarkerAsync(Amf0TypeMarkers.ObjectEnd);
        }

        /// <summary>
        /// 此方法写入类型标记和字符串
        /// </summary>
        /// <param name="obj"></param>
        internal void WriteAmf0TypedObject(object obj)
        {
            if (obj == null)
                throw new ArgumentNullException(nameof(obj));

            if (SerializationContext == null)
                throw new NullReferenceException("no serialization context was provided");

            AddAmf0Reference(obj);

            var type = obj.GetType();
            var typeName = type.FullName;

            var classDescription = SerializationContext.GetClassDescription(type, obj);
            if (classDescription == null)
                throw new SerializationException($"couldn't get class description for {typeName}");

            WriteMarker(Amf0TypeMarkers.TypedObject);
            WriteUtfPrefixed(classDescription.Name);
            foreach (var member in classDescription.Members)
            {
                WriteUtfPrefixed(member.SerializedName);
                WriteAmf0Item(member.GetValue(obj));
            }

            // 结束对象的零长度的字段名称来表示，然后结束对象类型的标记字段名长度前缀UTF8字符串，所以[ 0长度的字符串，结束对象类型标记]
            WriteUInt16(0);
            WriteMarker(Amf0TypeMarkers.ObjectEnd);
        }

        internal void WriteAmf0TypedObjectAsync(object obj)
        {
            if (obj == null)
                throw new ArgumentNullException(nameof(obj));

            if (SerializationContext == null)
                throw new NullReferenceException("no serialization context was provided");

            AddAmf0Reference(obj);

            var type = obj.GetType();
            var typeName = type.FullName;

            var classDescription = SerializationContext.GetClassDescription(type, obj);
            if (classDescription == null)
                throw new SerializationException($"couldn't get class description for {typeName}");

            WriteMarkerAsync(Amf0TypeMarkers.TypedObject);
            WriteUtfPrefixedAsync(classDescription.Name);
            foreach (var member in classDescription.Members)
            {
                WriteUtfPrefixedAsync(member.SerializedName);
                WriteAmf0ItemAsync(member.GetValue(obj));
            }

            //结束对象的零长度的字段名称来表示，然后结束对象类型的标记字段名长度前缀UTF8字符串，所以[ 0长度的字符串，结束对象类型标记]
            WriteUInt16Async(0);
            WriteMarkerAsync(Amf0TypeMarkers.ObjectEnd);
        }

        internal void WriteAmf0DateTime(DateTime value)
        {
            // http://download.macromedia.com/pub/labs/amf/amf0_spec_121207.pdf
            // """
            // While the design of this type reserves room for time zone offset information,
            // it should not be filled in, nor used, as it is unconventional to change time
            // zones when serializing dates on a network. It is suggested that the time zone
            // be queried independently as needed.
            //  -- AMF0 specification, 2.13 Date Type
            // """

            var time = value.ToUniversalTime();
            var posixTime = time.Subtract(Epoch);
            WriteDouble(posixTime.TotalMilliseconds);
            // reserved for time zone info, but not used according to spec.
            WriteUInt16(0);
        }

        internal void WriteAmf0DateTimeAsync(DateTime value)
        {
            // http://download.macromedia.com/pub/labs/amf/amf0_spec_121207.pdf
            // """
            // While the design of this type reserves room for time zone offset information,
            // it should not be filled in, nor used, as it is unconventional to change time
            // zones when serializing dates on a network. It is suggested that the time zone
            // be queried independently as needed.
            //  -- AMF0 specification, 2.13 Date Type
            // """

            var time = value.ToUniversalTime();
            var posixTime = time.Subtract(Epoch);
            WriteDoubleAsync(posixTime.TotalMilliseconds);
            // reserved for time zone info, but not used according to spec.
            WriteUInt16Async(0);
        }

        internal void WriteAmf0XDocument(XDocument document)
        {
            if (document == null)
                throw new ArgumentNullException(nameof(document));

            AddAmf0Reference(document);
            var xml = document.ToString();
            WriteAmf0UtfLong(xml);
        }

        internal void WriteAmf0XDocumentAsync(XDocument document)
        {
            if (document == null)
                throw new ArgumentNullException(nameof(document));

            AddAmf0Reference(document);
            var xml = document.ToString();
            WriteAmf0UtfLongAsync(xml);
        }

        internal void WriteAmf0XElement(XElement element)
        {
            if (element == null)
                throw new ArgumentNullException(nameof(element));

            AddAmf0Reference(element);
            var xml = element.ToString();
            WriteAmf0UtfLong(xml);
        }

        internal void WriteAmf0XElementAsync(XElement element)
        {
            if (element == null)
                throw new ArgumentNullException(nameof(element));

            AddAmf0Reference(element);
            var xml = element.ToString();
            WriteAmf0UtfLongAsync(xml);
        }

        internal void WriteAmf0Array(Array array)
        {
            if (array == null)
                throw new ArgumentNullException(nameof(array));

            AddAmf0Reference(array);
            WriteInt32(array.Length);
            foreach (var element in array)
                WriteAmf0Item(element);
        }

        internal void WriteAmf0ArrayAsync(Array array)
        {
            if (array == null)
                throw new ArgumentNullException(nameof(array));

            AddAmf0Reference(array);
            WriteInt32Async(array.Length);
            foreach (var element in array)
                WriteAmf0ItemAsync(element);
        }

        // this method writes the type marker and string
        internal void WriteAmf0AssociativeArray(IDictionary<string, object> dictionary)
        {
            if (dictionary == null)
                throw new ArgumentNullException(nameof(dictionary));

            AddAmf0Reference(dictionary);
            WriteMarker(Amf0TypeMarkers.EcmaArray);
            WriteInt32(dictionary.Count);
            foreach (var entry in dictionary)
            {
                WriteUtfPrefixed(entry.Key);
                WriteAmf0Item(entry.Value);
            }

            // 结束对象的零长度的字段名称来表示，然后结束对象类型的标记字段名长度前缀UTF8字符串，所以[ 0长度的字符串，结束对象类型标记]
            WriteUInt16(0);
            WriteMarker(Amf0TypeMarkers.ObjectEnd);
        }

        internal void WriteAmf0AssociativeArrayAsync(IDictionary<string, object> dictionary)
        {
            if (dictionary == null)
                throw new ArgumentNullException(nameof(dictionary));

            AddAmf0Reference(dictionary);
            WriteMarkerAsync(Amf0TypeMarkers.EcmaArray);
            WriteInt32Async(dictionary.Count);
            foreach (var entry in dictionary)
            {
                WriteUtfPrefixedAsync(entry.Key);
                WriteAmf0ItemAsync(entry.Value);
            }

            // 结束对象的零长度的字段名称来表示，然后结束对象类型的标记字段名长度前缀UTF8字符串，所以[ 0长度的字符串，结束对象类型标记]
            WriteUInt16Async(0);
            WriteMarkerAsync(Amf0TypeMarkers.ObjectEnd);
        }

        #endregion


        #region amf3

        void AddAmf3Reference(string obj)
        {
            AddAmf3Reference(amf3StringReferences, obj);
        }

        void AddAmf3Reference(object obj)
        {
            AddAmf3Reference(amf3ObjectReferences, obj);
        }

        void AddAmf3Reference(Dictionary<object, int> referenceDictionary, object obj)
        {
            referenceDictionary.Add(obj, referenceDictionary.Count);
        }

        // writes `value` with the `inline object flag`. the object contents is expected to be written after this header.
        void WriteAmf3InlineHeader(int value)
        {
            // 1 == inline object (not an object reference)
            WriteAmf3Int((value << 1) | 1);
        }

        void WriteAmf3InlineHeaderAsync(int value)
        {
            // 1 == inline object (not an object reference)
            WriteAmf3IntAsync((value << 1) | 1);
        }

        // if `obj` has already been written, then write the reference and returns true. If no object was written, returns false.
        bool WriteAmf3ReferenceOnExistence(string obj)
        {
            return WriteAmf3ReferenceOnExistence(amf3StringReferences, obj);
        }

        bool WriteAmf3ReferenceOnExistenceAsync(string obj)
        {
            return WriteAmf3ReferenceOnExistenceAsync(amf3StringReferences, obj);
        }

        bool WriteAmf3ReferenceOnExistence(object obj)
        {
            return WriteAmf3ReferenceOnExistence(amf3ObjectReferences, obj);
        }

        bool WriteAmf3ReferenceOnExistenceAsync(object obj)
        {
            return WriteAmf3ReferenceOnExistenceAsync(amf3ObjectReferences, obj);
        }

        bool WriteAmf3ReferenceOnExistence(Dictionary<object, int> referenceDictionary, object obj)
        {
            int index;
            if (!referenceDictionary.TryGetValue(obj, out index))
                return false;

            // 0 == not inline (an object reference)
            WriteAmf3Int(index << 1);
            return true;
        }

        bool WriteAmf3ReferenceOnExistenceAsync(Dictionary<object, int> referenceDictionary, object obj)
        {
            int index;
            if (!referenceDictionary.TryGetValue(obj, out index))
                return false;

            // 0 == not inline (an object reference)
            WriteAmf3IntAsync(index << 1);
            return true;
        }

        // this method writes the type marker and string
        public void WriteAmf3Item(object data)
        {
            // if it's null, we don't need to do expensive operations to determine how to write it
            if (data == null)
            {
                WriteAmf3Null();
                return;
            }

            var type = data.GetType();
            var writer = GetAmfWriter(Amf3Writers, type);
            writer.WriteData(this, data);
        }

        public void WriteAmf3ItemAsync(object data)
        {
            // if it's null, we don't need to do expensive operations to determine how to write it
            if (data == null)
            {
                WriteAmf3NullAsync();
                return;
            }

            var type = data.GetType();
            var writer = GetAmfWriter(Amf3Writers, type);
            writer.WriteDataAsync(this, data);
        }

        // this method writes the type marker and string
        internal void WriteAmf3Null()
        {
            WriteMarker(Amf3TypeMarkers.Null);
        }

        internal void WriteAmf3NullAsync()
        {
            WriteMarkerAsync(Amf3TypeMarkers.Null);
        }

        // this method writes the type marker and string
        internal void WriteAmf3BoolSpecial(bool value)
        {
            WriteMarker(value ? Amf3TypeMarkers.True : Amf3TypeMarkers.False);
        }

        internal void WriteAmf3BoolSpecialAsync(bool value)
        {
            WriteMarkerAsync(value ? Amf3TypeMarkers.True : Amf3TypeMarkers.False);
        }

        internal void WriteAmf3Array(Array array)
        {
            if (array == null)
                throw new ArgumentNullException(nameof(array));

            if (WriteAmf3ReferenceOnExistence(array))
                return;

            AddAmf3Reference(array);
            WriteAmf3InlineHeader(array.Length);

            // empty key signifies end of associative section of array
            WriteAmf3Utf(string.Empty);

            foreach (var element in array)
                WriteAmf3Item(element);
        }

        internal void WriteAmf3ArrayAsync(Array array)
        {
            if (array == null)
                throw new ArgumentNullException(nameof(array));

            if (WriteAmf3ReferenceOnExistenceAsync(array))
                return;

            AddAmf3Reference(array);
            WriteAmf3InlineHeaderAsync(array.Length);

            // empty key signifies end of associative section of array
            WriteAmf3UtfAsync(string.Empty);

            foreach (var element in array)
                WriteAmf3ItemAsync(element);
        }

        internal void WriteAmf3Array(IEnumerable enumerable)
        {
            if (enumerable == null)
                throw new ArgumentNullException(nameof(enumerable));

            if (WriteAmf3ReferenceOnExistence(enumerable))
                return;

            var list = enumerable.ToList();
            AddAmf3Reference(list);

            // number of dense items.
            WriteAmf3InlineHeader(list.Count);

            // empty key signifies end of associative section of array
            WriteAmf3Utf(string.Empty);

            foreach (var element in list)
                WriteAmf3Item(element);
        }

        internal void WriteAmf3ArrayAsync(IEnumerable enumerable)
        {
            if (enumerable == null)
                throw new ArgumentNullException(nameof(enumerable));

            if (WriteAmf3ReferenceOnExistence(enumerable))
                return;

            var list = enumerable.ToList();
            AddAmf3Reference(list);

            // number of dense items.
            WriteAmf3InlineHeaderAsync(list.Count);

            // empty key signifies end of associative section of array
            WriteAmf3UtfAsync(string.Empty);

            foreach (var element in list)
                WriteAmf3ItemAsync(element);
        }

        internal void WriteAmf3AssociativeArray(IDictionary<string, object> dictionary)
        {
            if (dictionary == null)
                throw new ArgumentNullException(nameof(dictionary));

            if (WriteAmf3ReferenceOnExistence(dictionary))
                return;

            AddAmf3Reference(dictionary);

            // number of dense items - zero for an associative array.
            WriteAmf3InlineHeader(0);

            foreach (var pair in dictionary)
            {
                WriteAmf3Utf(pair.Key);
                WriteAmf3Item(pair.Value);
            }

            // empty key signifies end of associative section of array
            WriteAmf3Utf(string.Empty);
        }

        internal void WriteAmf3AssociativeArrayAsync(IDictionary<string, object> dictionary)
        {
            if (dictionary == null)
                throw new ArgumentNullException(nameof(dictionary));

            if (WriteAmf3ReferenceOnExistenceAsync(dictionary))
                return;

            AddAmf3Reference(dictionary);

            // number of dense items - zero for an associative array.
            WriteAmf3InlineHeaderAsync(0);

            foreach (var pair in dictionary)
            {
                WriteAmf3UtfAsync(pair.Key);
                WriteAmf3ItemAsync(pair.Value);
            }

            // empty key signifies end of associative section of array
            WriteAmf3UtfAsync(string.Empty);
        }

        internal void WriteAmf3ByteArray(ByteArray array)
        {
            if (array == null)
                throw new ArgumentNullException(nameof(array));

            if (WriteAmf3ReferenceOnExistence(array))
                return;

            AddAmf3Reference(array);
            WriteAmf3InlineHeader((int)array.Length);
            WriteBytes(array.MemoryStream.ToArray());
        }

        internal void WriteAmf3ByteArrayAsync(ByteArray array)
        {
            if (array == null)
                throw new ArgumentNullException(nameof(array));

            if (WriteAmf3ReferenceOnExistenceAsync(array))
                return;

            AddAmf3Reference(array);
            WriteAmf3InlineHeaderAsync((int)array.Length);
            WriteBytesAsync(array.MemoryStream.ToArray());
        }

        internal void WriteAmf3Utf(string str)
        {
            if (str == null)
                throw new ArgumentNullException(nameof(str));

            if (str == string.Empty)
            {
                // zero length strings are never sent by reference.
                WriteAmf3InlineHeader(0);
                return;
            }

            if (WriteAmf3ReferenceOnExistence(str))
                return;

            AddAmf3Reference(str);
            var bytes = Encoding.UTF8.GetBytes(str);
            WriteAmf3InlineHeader(bytes.Length);
            WriteBytes(bytes);
        }

        internal void WriteAmf3UtfAsync(string str)
        {
            if (str == null)
                throw new ArgumentNullException(nameof(str));

            if (str == string.Empty)
            {
                // zero length strings are never sent by reference.
                WriteAmf3InlineHeaderAsync(0);
                return;
            }

            if (WriteAmf3ReferenceOnExistenceAsync(str))
                return;

            AddAmf3Reference(str);
            var bytes = Encoding.UTF8.GetBytes(str);
            WriteAmf3InlineHeaderAsync(bytes.Length);
            WriteBytesAsync(bytes);
        }

        internal void WriteAmf3DateTime(DateTime value)
        {
            if (WriteAmf3ReferenceOnExistence(value))
                return;

            var time = value.ToUniversalTime();
            var posixTime = time.Subtract(Epoch);
            // not used except to denote inline object
            WriteAmf3InlineHeader(0);
            WriteDouble((double)posixTime.TotalMilliseconds);
        }

        internal void WriteAmf3DateTimeAsync(DateTime value)
        {
            if (WriteAmf3ReferenceOnExistence(value))
                return;

            var time = value.ToUniversalTime();
            var posixTime = time.Subtract(Epoch);
            // not used except to denote inline object
            WriteAmf3InlineHeaderAsync(0);
            WriteDoubleAsync((double)posixTime.TotalMilliseconds);
        }

        // when writing, sign does not matter.
        internal void WriteAmf3Int(int value)
        {
            // sign contraction - the high order bit of the resulting value must match every bit removed from the number
            // clear 3 bits
            value = value & 0x1fffffff;
            if (value < 0x80)
            {
                WriteByte((byte)value);
            }
            else if (value < 0x4000)
            {
                WriteByte((byte)(value >> 7 & 0x7f | 0x80));
                WriteByte((byte)(value & 0x7f));
            }
            else if (value < 0x200000)
            {
                WriteByte((byte)(value >> 14 & 0x7f | 0x80));
                WriteByte((byte)(value >> 7 & 0x7f | 0x80));
                WriteByte((byte)(value & 0x7f));
            }
            else
            {
                WriteByte((byte)(value >> 22 & 0x7f | 0x80));
                WriteByte((byte)(value >> 15 & 0x7f | 0x80));
                WriteByte((byte)(value >> 8 & 0x7f | 0x80));
                WriteByte((byte)(value & 0xff));
            }
        }

        internal void WriteAmf3IntAsync(int value)
        {
            // sign contraction - the high order bit of the resulting value must match every bit removed from the number
            // clear 3 bits
            value = value & 0x1fffffff;
            if (value < 0x80)
            {
                WriteByteAsync((byte)value);
            }
            else if (value < 0x4000)
            {
                WriteByteAsync((byte)(value >> 7 & 0x7f | 0x80));
                WriteByteAsync((byte)(value & 0x7f));
            }
            else if (value < 0x200000)
            {
                WriteByteAsync((byte)(value >> 14 & 0x7f | 0x80));
                WriteByteAsync((byte)(value >> 7 & 0x7f | 0x80));
                WriteByteAsync((byte)(value & 0x7f));
            }
            else
            {
                WriteByteAsync((byte)(value >> 22 & 0x7f | 0x80));
                WriteByteAsync((byte)(value >> 15 & 0x7f | 0x80));
                WriteByteAsync((byte)(value >> 8 & 0x7f | 0x80));
                WriteByteAsync((byte)(value & 0xff));
            }
        }

        // this method writes the type marker and string
        internal void WriteAmf3NumberSpecial(int value)
        {
            // write numbers that are out of range as a double.
            if (value >= Int29Range[0] && value <= Int29Range[1])
            {
                WriteMarker(Amf3TypeMarkers.Integer);
                WriteAmf3Int(value);
            }
            else
            {
                WriteMarker(Amf3TypeMarkers.Double);
                WriteAmf3Double((double)value);
            }
        }

        internal void WriteAmf3NumberSpecialAsync(int value)
        {
            // write numbers that are out of range as a double.
            if (value >= Int29Range[0] && value <= Int29Range[1])
            {
                WriteMarkerAsync(Amf3TypeMarkers.Integer);
                WriteAmf3IntAsync(value);
            }
            else
            {
                WriteMarkerAsync(Amf3TypeMarkers.Double);
                WriteAmf3DoubleAsync((double)value);
            }
        }

        internal void WriteAmf3Double(double value)
        {
            WriteDouble(value);
        }

        internal void WriteAmf3DoubleAsync(double value)
        {
            WriteDoubleAsync(value);
        }

        internal void WriteAmf3XDocument(XDocument document)
        {
            //if (document == null)
            //    throw new ArgumentNullException("document");

            WriteAmf3Utf(document?.ToString() ?? string.Empty);
        }

        internal void WriteAmf3XDocumentAsync(XDocument document)
        {
            //if (document == null)
            //    throw new ArgumentNullException("document");

            WriteAmf3UtfAsync(document?.ToString() ?? string.Empty);
        }

        internal void WriteAmf3XElement(XElement element)
        {
            //if (element == null)
            //    throw new ArgumentNullException("element");

            WriteAmf3Utf(element?.ToString() ?? string.Empty);
        }

        internal void WriteAmf3XElementAsync(XElement element)
        {
            //if (element == null)
            //    throw new ArgumentNullException("element");

            WriteAmf3UtfAsync(element?.ToString() ?? string.Empty);
        }

        internal void WriteAmf3VectorAsync<T>(bool writeTypeName, bool fixedSize, IList list, Action<T> writeElement)
        {
            if (list == null)
                throw new ArgumentNullException(nameof(list));

            if (WriteAmf3ReferenceOnExistenceAsync(list))
                return;

            AddAmf3Reference(list);
            WriteAmf3InlineHeaderAsync(list.Count);

            WriteByteAsync((byte)(fixedSize ? 1 : 0));
            // the "any type"
            if (writeTypeName)
                WriteAmf3UtfAsync("*");
            foreach (var item in list)
                writeElement((T)item);
        }

        internal void WriteAmf3Vector<T>(bool writeTypeName, bool fixedSize, IList list, Action<T> writeElement)
        {
            if (list == null)
                throw new ArgumentNullException(nameof(list));

            if (WriteAmf3ReferenceOnExistence(list))
                return;

            AddAmf3Reference(list);
            WriteAmf3InlineHeader(list.Count);

            WriteByte((byte)(fixedSize ? 1 : 0));
            // the "any type"
            if (writeTypeName)
                WriteAmf3Utf("*");
            foreach (var item in list)
                writeElement((T)item);
        }

        internal void WriteAmf3Dictionary(IDictionary dictionary)
        {
            if (dictionary == null)
                throw new ArgumentNullException(nameof(dictionary));

            if (WriteAmf3ReferenceOnExistence(dictionary))
                return;

            AddAmf3Reference(dictionary);
            WriteAmf3InlineHeader(dictionary.Count);

            // we don't support weakly referenced pairs (yet) - always use strong references
            WriteByte(0);
            var enumerator = dictionary.GetEnumerator();
            while (enumerator.MoveNext())
            {
                WriteAmf3Item(enumerator.Key);
                WriteAmf3Item(enumerator.Value);
            }
        }

        internal void WriteAmf3DictionaryAsync(IDictionary dictionary)
        {
            if (dictionary == null)
                throw new ArgumentNullException(nameof(dictionary));

            if (WriteAmf3ReferenceOnExistenceAsync(dictionary))
                return;

            AddAmf3Reference(dictionary);
            WriteAmf3InlineHeaderAsync(dictionary.Count);

            // we don't support weakly referenced pairs (yet) - always use strong references
            WriteByteAsync(0);
            var enumerator = dictionary.GetEnumerator();
            while (enumerator.MoveNext())
            {
                WriteAmf3ItemAsync(enumerator.Key);
                WriteAmf3ItemAsync(enumerator.Value);
            }
        }


        internal void WriteAmf3Object(object obj)
        {
            if (obj == null)
                throw new ArgumentNullException(nameof(obj));

            if (SerializationContext == null)
                throw new NullReferenceException("no serialization context was provided");

            if (WriteAmf3ReferenceOnExistence(obj))
                return;

            AddAmf3Reference(obj);

            var classDescription = SerializationContext.GetClassDescription(obj);
            int existingDefinitionIndex;
            if (amf3ClassDefinitionReferences.TryGetValue(classDescription, out existingDefinitionIndex))
            {
                // http://download.macromedia.com/pub/labs/amf/amf3_spec_121207.pdf
                // """
                // The first (low) bit is a flag with value 1. The second bit is a flag
                // (representing whether a trait reference follows) with value 0 to imply that
                // this objects traits are  being sent by reference. The remaining 1 to 27
                // significant bits are used to encode a trait reference index (an  integer).
                // -- AMF3 specification, 3.12 Object type
                // """

                // <u27=trait-reference-index> <0=trait-reference> <1=object-inline>
                WriteAmf3InlineHeader(existingDefinitionIndex << 1);
            }
            else
            {
                amf3ClassDefinitionReferences.Add(classDescription, amf3ClassDefinitionReferences.Count);

                // write the class definition
                // we can use the same format to serialize normal and extern classes, for simplicity's sake.
                //     normal:         <u25=member-count> <u1=dynamic> <0=externalizable> <1=trait-inline> <1=object-inline>
                //     externalizable: <u25=insignificant> <u1=insignificant> <1=externalizable> <1=trait-inline> <1=object-inline>
                var header = classDescription.Members.Length;
                header = (header << 1) | (classDescription.IsDynamic ? 1 : 0);
                header = (header << 1) | (classDescription.IsExternalizable ? 1 : 0);
                header = (header << 1) | 1;
                // last shift done in this method
                WriteAmf3InlineHeader(header);
                WriteAmf3Utf(classDescription.Name);

                // write object
                if (classDescription.IsExternalizable)
                {
                    var externalizable = obj as IExternalizable;
                    if (externalizable == null)
                    {
                        var type = obj.GetType();
                        throw new SerializationException($"{type.FullName} ({classDescription.Name}) is marked as externalizable but does not implement IExternalizable");
                    }

                    externalizable.WriteExternal(new DataOutput(this));
                }
                else
                {
                    foreach (var member in classDescription.Members)
                        WriteAmf3Utf(member.SerializedName);

                    foreach (var member in classDescription.Members)
                        WriteAmf3Item(member.GetValue(obj));

                    if (classDescription.IsDynamic)
                    {
                        var dictionary = obj as IDictionary<string, object>;
                        if (dictionary == null)
                        {
                            var type = obj.GetType();
                            throw new SerializationException($"{type.FullName} is marked as dynamic but does not implement IDictionary");
                        }

                        foreach (var entry in dictionary)
                        {
                            WriteAmf3Utf(entry.Key);
                            WriteAmf3Item(entry.Value);
                        }

                        WriteAmf3Utf(string.Empty);
                    }
                }
            }
        }

        internal void WriteAmf3ObjectAsync(object obj)
        {
            if (obj == null)
                throw new ArgumentNullException(nameof(obj));

            if (SerializationContext == null)
                throw new NullReferenceException("no serialization context was provided");

            if (WriteAmf3ReferenceOnExistence(obj))
                return;

            AddAmf3Reference(obj);

            var classDescription = SerializationContext.GetClassDescription(obj);
            int existingDefinitionIndex;
            if (amf3ClassDefinitionReferences.TryGetValue(classDescription, out existingDefinitionIndex))
            {
                // http://download.macromedia.com/pub/labs/amf/amf3_spec_121207.pdf
                // """
                // The first (low) bit is a flag with value 1. The second bit is a flag
                // (representing whether a trait reference follows) with value 0 to imply that
                // this objects traits are  being sent by reference. The remaining 1 to 27
                // significant bits are used to encode a trait reference index (an  integer).
                // -- AMF3 specification, 3.12 Object type
                // """

                // <u27=trait-reference-index> <0=trait-reference> <1=object-inline>
                WriteAmf3InlineHeaderAsync(existingDefinitionIndex << 1);
            }
            else
            {
                amf3ClassDefinitionReferences.Add(classDescription, amf3ClassDefinitionReferences.Count);

                // write the class definition
                // we can use the same format to serialize normal and extern classes, for simplicity's sake.
                //     normal:         <u25=member-count> <u1=dynamic> <0=externalizable> <1=trait-inline> <1=object-inline>
                //     externalizable: <u25=insignificant> <u1=insignificant> <1=externalizable> <1=trait-inline> <1=object-inline>
                var header = classDescription.Members.Length;
                header = (header << 1) | (classDescription.IsDynamic ? 1 : 0);
                header = (header << 1) | (classDescription.IsExternalizable ? 1 : 0);
                header = (header << 1) | 1;
                // last shift done in this method
                WriteAmf3InlineHeaderAsync(header);
                WriteAmf3UtfAsync(classDescription.Name);

                // write object
                if (classDescription.IsExternalizable)
                {
                    var externalizable = obj as IExternalizable;
                    if (externalizable == null)
                    {
                        var type = obj.GetType();
                        throw new SerializationException($"{type.FullName} ({classDescription.Name}) is marked as externalizable but does not implement IExternalizable");
                    }

                    externalizable.WriteExternal(new DataOutput(this));
                }
                else
                {
                    foreach (var member in classDescription.Members)
                        WriteAmf3UtfAsync(member.SerializedName);

                    foreach (var member in classDescription.Members)
                        WriteAmf3ItemAsync(member.GetValue(obj));

                    if (classDescription.IsDynamic)
                    {
                        var dictionary = obj as IDictionary<string, object>;
                        if (dictionary == null)
                        {
                            var type = obj.GetType();
                            throw new SerializationException($"{type.FullName} is marked as dynamic but does not implement IDictionary");
                        }

                        foreach (var entry in dictionary)
                        {
                            WriteAmf3UtfAsync(entry.Key);
                            WriteAmf3ItemAsync(entry.Value);
                        }

                        WriteAmf3UtfAsync(string.Empty);
                    }
                }
            }
        }

        #endregion
    }
}
