using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace FunscriptToolbox.Core
{
    public class Funscript
    {
        public const string AudioSignatureExtension = ".asig";
        public const string FunscriptExtension = ".funscript";

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

        public FunscriptAction[] Actions
        {
            get
            {
                return r_content.actions?.ToObject<FunscriptAction[]>();
            }
            set
            {
                r_content.actions = new JArray(value.Select(f => JObject.FromObject(f)).ToArray());
            }
        }

        public FunscriptAudioSignature AudioSignature { 
            get 
            {
                return r_content.audioSignature?.ToObject<FunscriptAudioSignature>();
            }
            set 
            {
                r_content.audioSignature = JObject.FromObject(value);
            }
        }

        public void AddNotes(string newNote)
        {
            if (r_content.metadata == null)
                r_content.metadata = JObject.FromObject(new { notes = string.Empty });

            var originalNotes = r_content.metadata.notes;
            if (string.IsNullOrWhiteSpace(originalNotes?.Value))
            {
                r_content.metadata.notes = newNote;
            }
            else
            {
                r_content.metadata.notes = originalNotes + "\n" + newNote;
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
