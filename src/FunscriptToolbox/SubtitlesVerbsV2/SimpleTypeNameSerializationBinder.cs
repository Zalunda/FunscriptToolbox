using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbV2
{
    public class SimpleTypeNameSerializationBinder : DefaultSerializationBinder
    {
        private Dictionary<string, Type> _nameToType;
        private Dictionary<Type, string> _typeToName;

        public SimpleTypeNameSerializationBinder(Type[] baseTypes)
        {
            var matchingTypes =
                this.GetType()
                    .Assembly
                    .GetTypes()
                    .Where(x => baseTypes.Any(
                        baseType => baseType.IsAssignableFrom(x) && !x.IsAbstract));

            _nameToType = matchingTypes.ToDictionary(
                t => t.Name,
                t => t);
            _typeToName = _nameToType.ToDictionary(
                t => t.Value,
                t => t.Key);
        }

        public override void BindToName(Type serializedType, out string assemblyName, out string typeName)
        {
            if (_typeToName.TryGetValue(serializedType, out typeName))
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
            if (_nameToType.TryGetValue(typeName, out var matchedType))
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