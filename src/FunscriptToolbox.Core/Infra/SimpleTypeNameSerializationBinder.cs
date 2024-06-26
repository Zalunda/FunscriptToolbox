﻿using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.Core.Infra
{
    public class SimpleTypeNameSerializationBinder : DefaultSerializationBinder
    {
        private readonly Dictionary<string, Type> r_nameToType;
        private readonly Dictionary<Type, string> r_typeToName;

        public SimpleTypeNameSerializationBinder(Type[] baseTypes)
        {
            var matchingTypes =
                AppDomain
                .CurrentDomain
                .GetAssemblies()
                .SelectMany(assembly => assembly.GetTypes())
                .Where(x => baseTypes.Any(
                    baseType => baseType.IsAssignableFrom(x) && !x.IsAbstract));

            r_nameToType = matchingTypes.ToDictionary(
                t => SimplifyName(t.Name, baseTypes),
                t => t);
            r_typeToName = r_nameToType.ToDictionary(
                t => t.Value,
                t => t.Key);
        }

        private string SimplifyName(string originalName, Type[] baseTypes)
        {
            var simplifiedName = originalName;
            foreach (var baseType in baseTypes) 
            {
                if (baseType.IsAbstract)
                {
                    simplifiedName = simplifiedName.Replace(baseType.Name, string.Empty);
                }
            }
            return simplifiedName;
        }

        public override void BindToName(Type serializedType, out string assemblyName, out string typeName)
        {
            if (r_typeToName.TryGetValue(serializedType, out typeName))
            {
                assemblyName = null;
            }
            else
            {
                base.BindToName(serializedType, out assemblyName, out typeName);
            }
        }

        public override Type BindToType(string assemblyName, string typeName)
        {
            if (r_nameToType.TryGetValue(typeName, out var matchedType))
            {
                return matchedType;
            }
            else
            {
                return base.BindToType(assemblyName, typeName);
            }
        }
    }
}