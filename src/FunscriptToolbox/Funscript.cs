using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace AudioSynchronization
{
    public class Funscript
    {
        private readonly static JsonSerializer rs_serializer = JsonSerializer
            .Create(new JsonSerializerSettings
                {
                    Formatting = Formatting.None,
                    ContractResolver = new CamelCasePropertyNamesContractResolver()
                });

        public static Funscript FromFile(string filename)
        {
            using (var reader = File.OpenText(filename))
            using (var jsonReader = new JsonTextReader(reader))
            {
                var content = rs_serializer.Deserialize<dynamic>(jsonReader);
                return new Funscript(content);
            }
        }

        public dynamic r_content;

        public Funscript(dynamic content = null)
        {
            r_content = content ?? new JObject();
        }

        public FunscriptActions[] Actions
        {
            get
            {
                return r_content.actions.ToObject<FunscriptActions[]>();
            }
            set
            {
                r_content.actions = new JArray(value.Select(f => JObject.FromObject(f)).ToArray());
            }
        }

        public AudioSignature AudioSignature { 
            get 
            {
                return r_content.audioSignature.ToObject<AudioSignature>();
            }
            set 
            {
                r_content.audioSignature = JObject.FromObject(value);
            }
        }

        public void Save(string filename)
        {
            using (var writer = File.CreateText(filename))
            {
                rs_serializer.Serialize(writer, r_content);
            }
        }
    }
}
