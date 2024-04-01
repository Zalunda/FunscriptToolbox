using FunscriptToolbox.SubtitlesVerbsV2.Transcriptions;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbV2
{
    [JsonObject(IsReference = false)]
    public class WorkInProgressSubtitles
    {
        private readonly static JsonSerializer rs_serializer = JsonSerializer
            .Create(new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                PreserveReferencesHandling = PreserveReferencesHandling.Objects,
                ContractResolver = new CamelCasePropertyNamesContractResolver()
            });
        public const string Extension = ".wipsubtitles";
        static WorkInProgressSubtitles()
        {
            rs_serializer.Converters.Add(new StringEnumConverter());
        }

        public static WorkInProgressSubtitles FromFile(string filepath)
        {
            using var reader = File.OpenText(filepath);
            using var jsonReader = new JsonTextReader(reader);
            var content = rs_serializer.Deserialize<WorkInProgressSubtitles>(jsonReader);
            content.OriginalFilePath = filepath;
            return content;
        }

        public WorkInProgressSubtitles()
        {
            this.Transcriptions = new List<Transcription>();
        }

        public WorkInProgressSubtitles(string fullpath)           
            : this()
        {
            OriginalFilePath = fullpath;
        }

        [JsonIgnore]
        public string OriginalFilePath { get; private set; }

        public string FormatVersion { get; set; } = "1.0";
        public PcmAudio PcmAudio { get; set; }
        public SubtitleForcedTimingCollection SubtitlesForcedTiming { get; set; }
        public List<Transcription> Transcriptions { get; set; }

        public void Save(string filepath = null)
        {
            var path = filepath ?? OriginalFilePath;
            using (var writer = new StreamWriter(path + ".temp", false, Encoding.UTF8))
            {
                rs_serializer.Serialize(writer, this);
            }

            if (File.Exists(path))
            {
                File.Delete(path + ".bak");
                File.Move(path, path + ".bak");
            }
            File.Move(path + ".temp", path);
        }
    }
}
