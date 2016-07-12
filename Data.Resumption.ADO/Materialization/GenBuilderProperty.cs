﻿using System;
using Data.Resumption.ADO.Materialization.GenBuilderProperties;

namespace Data.Resumption.ADO.Materialization
{
    internal static class GenBuilderProperty
    {
        public static IGenBuilderProperty GetProperty(string name, Type propertyType)
        {
            if (PrimitiveConverter.IsPrimitive(propertyType))
            {
                return new PrimitiveGenBuilderProperty(name, propertyType);
            }
            // TODO better handling, support many more collection types
            if (propertyType.IsArray)
            {
                return new ManyNavGenBuilderProperty(name, propertyType.GetElementType());
            }
            return new SingleNavGenBuilderProperty(name, propertyType);
        }
    }
}
