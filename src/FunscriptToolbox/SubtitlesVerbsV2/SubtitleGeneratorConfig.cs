using FunscriptToolbox.SubtitlesVerbsV2.Transcriptions;
using FunscriptToolbox.SubtitlesVerbsV2.Translations;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.IO;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbV2
{
    internal class SubtitleGeneratorConfig
    {
        public static readonly JsonSerializer rs_serializer = JsonSerializer
                .Create(new JsonSerializerSettings
                {
                    Formatting = Formatting.Indented,
                    SerializationBinder = new SimpleTypeNameSerializationBinder(
                        new[] {
                            typeof(Transcriber),
                            typeof(Translator),
                            typeof(WhisperConfig)
                        }),
                    TypeNameHandling = TypeNameHandling.Auto        
                });
        static SubtitleGeneratorConfig()
        {
            rs_serializer.Converters.Add(new StringEnumConverter());
        }

        public static SubtitleGeneratorConfig FromFile(string filepath)
        {
            using var reader = File.OpenText(filepath);
            using var jsonReader = new JsonTextReader(reader);
            return rs_serializer.Deserialize<SubtitleGeneratorConfig>(jsonReader);
        }

        public SubtitleGeneratorConfig()
        {
        }

        public string SubtitleForcedLocationSuffix { get; set; }
        public string FfmpegAudioExtractionParameters { get; set; }
        public Transcriber[] Transcribers { get; set; }

        // Only for debug
        public void Save(string filepath)
        {
            using (var writer = new StreamWriter(filepath, false, Encoding.UTF8))
            {
                rs_serializer.Serialize(writer, this);
            }
        }
    }
}