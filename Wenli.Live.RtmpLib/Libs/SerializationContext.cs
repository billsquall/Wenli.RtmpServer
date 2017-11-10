﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Wenli.Live.Common;
using Wenli.Live.RtmpLib.Libs;
using Wenli.Live.RtmpLib.Models;

namespace Wenli.Live.RtmpLib.Libs
{
    public class SerializationContext
    {
        // 指定类型不能序列化为类型化对象时采取的操作过程（因为尚未注册）
        readonly FallbackStrategy fallbackStrategy;
        readonly SerializerObjectFactory serializerObjectFactory;
        readonly ObjectWrapperFactory objectWrapperFactory;



        public SerializationContext()
        {
            this.fallbackStrategy = FallbackStrategy.DynamicObject;
            this.serializerObjectFactory = new SerializerObjectFactory();
            this.objectWrapperFactory = new ObjectWrapperFactory(this);
        }

        public SerializationContext(IEnumerable<Type> types) : this()
        {
            foreach (var type in types)
                Register(type);
        }

        public SerializationContext(FallbackStrategy fallbackStrategy) : this()
        {
            this.fallbackStrategy = fallbackStrategy;
        }

        public SerializationContext(FallbackStrategy fallbackStrategy, IEnumerable<Type> types) : this(fallbackStrategy)
        {
            foreach (var type in types)
                Register(type);
        }

        public void Register(Type type) { serializerObjectFactory.Register(type); }
        public void RegisterAlias(Type type, string alias, bool canonical) { serializerObjectFactory.RegisterAlias(type, alias, canonical); }

        internal string GetAlias(string typeName) => serializerObjectFactory.GetAlias(typeName);

        internal bool CanCreate(string typeName) => serializerObjectFactory.CanCreate(typeName);
        internal bool CanCreate(Type type) => serializerObjectFactory.CanCreate(type);

        internal object Create(string typeName) => serializerObjectFactory.Create(typeName);
        internal object Create(Type type) => serializerObjectFactory.Create(type);


        internal ClassDescription GetClassDescription(object obj) => objectWrapperFactory.GetClassDescription(obj);
        internal ClassDescription GetClassDescription(Type type, object obj) => objectWrapperFactory.GetClassDescription(type, obj);


        internal DeserializationStrategy GetDeserializationStrategy(string typeName)
            => CanCreate(typeName)
                ? DeserializationStrategy.TypedObject
                : GetFallbackDeserializationStrategy();

        internal DeserializationStrategy GetDeserializationStrategy(Type type)
            => CanCreate(type)
                ? DeserializationStrategy.TypedObject
                : GetFallbackDeserializationStrategy();

        DeserializationStrategy GetFallbackDeserializationStrategy()
             => fallbackStrategy == FallbackStrategy.DynamicObject
                ? DeserializationStrategy.DynamicObject
                : DeserializationStrategy.Exception;
    }
}
