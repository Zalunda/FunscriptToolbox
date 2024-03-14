using FunscriptToolbox.SubtitlesVerbsV2.Transcription;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using System.IO;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbV2
{
    [JsonObject(IsReference = false)]
    class WorkInProgressSubtitles
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
            this.Transcriptions = new FullTranscriptionCollection();
        }

        public WorkInProgressSubtitles(string fullpath)           
            : this()
        {
            OriginalFilePath = fullpath;
        }

        [JsonIgnore]
        public string OriginalFilePath { get; private set; }

        public PcmAudio PcmAudio { get; set; }
        public FinalSubtitleLocationCollection FinalSubtitlesLocation { get; set; }
        public FullTranscriptionCollection Transcriptions { get; set; }

        public void Save(string filepath = null)
        {
            using var writer = new StreamWriter(filepath ?? OriginalFilePath, false, Encoding.UTF8);
            rs_serializer.Serialize(writer, this);
        }
    }
}
