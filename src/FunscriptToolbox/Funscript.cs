using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace AudioSynchronization
{
    public class Funscript
    {
        private readonly static JsonSerializer rs_serializer = JsonSerializer
            .Create(new JsonSerializerSettings
                {
                    Formatting = Formatting.Indented,
                    ContractResolver = new CamelCasePropertyNamesContractResolver()
                });

        public static Funscript FromFile(string filename)
        {
            using (var reader = File.OpenText(filename))
            using (var jsonReader = new JsonTextReader(reader))
            {
                return rs_serializer.Deserialize<Funscript>(jsonReader);
            }
        }

        public AudioSignature AudioSignature { get; set; }

        public void Save(string filename)
        {
            using (var writer = File.CreateText(filename))
            {
                rs_serializer.Serialize(writer, this);
            }
        }
    }
}
