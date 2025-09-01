using FunscriptToolbox.SubtitlesVerbs.AudioExtraction;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using FunscriptToolbox.SubtitlesVerbs.Transcriptions;
using FunscriptToolbox.SubtitlesVerbs.Translations;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbs
{
    [JsonObject(IsReference = false)]
    public class WorkInProgressSubtitles
    {
        public const string CURRENT_FORMAT_VERSION = "2.0";

        private readonly static JsonSerializer rs_serializer = JsonSerializer
            .Create(new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                PreserveReferencesHandling = PreserveReferencesHandling.Objects
            });
        public const string Extension = ".wipsubtitles";
        static WorkInProgressSubtitles()
        {
            rs_serializer.Converters.Add(new StringEnumConverter());
        }

        public static WorkInProgressSubtitles FromFile(string filepath, string videoPath)
        {
            try 
            {
                using var reader = File.OpenText(filepath);
                using var jsonReader = new JsonTextReader(reader);
                var content = rs_serializer.Deserialize<WorkInProgressSubtitles>(jsonReader);
                content.OriginalFilePath = filepath;
                content.OriginalVideoPath = videoPath;
                return content;
            }
            catch (Exception ex)
            {
                ex.Data.Add("File", filepath);
                throw new Exception($"Error parsing file '{filepath}': {ex.Message}", ex);
            }
        }

        public WorkInProgressSubtitles()
        {
            this.Transcriptions = new List<Transcription>();
            this.Translations = new List<Translation>();
        }

        public WorkInProgressSubtitles(string fullpath, string videoPath)
            : this()
        {
            OriginalFilePath = fullpath;
            OriginalVideoPath = videoPath;
        }

        [JsonIgnore]
        public string OriginalFilePath { get; private set; }
        [JsonIgnore]
        public string OriginalVideoPath { get; private set; }
        public string FormatVersion { get; set; } = CURRENT_FORMAT_VERSION;
        public PcmAudio PcmAudio { get; set; }
        public List<Transcription> Transcriptions { get; set; }
        public List<Translation> Translations { get; set; }
        public IEnumerable<TimedItemWithMetadataCollection> WorkersResult => ((IEnumerable<TimedItemWithMetadataCollection>)this.Transcriptions ?? Array.Empty<TimedItemWithMetadataCollection>())
            .Union((IEnumerable<TimedItemWithMetadataCollection>)this.Translations ?? Array.Empty<TimedItemWithMetadataCollection>());

        public void UpdateFormatVersion()
        {
            this.FormatVersion = CURRENT_FORMAT_VERSION;
        }

        public void Save(string filepath = null)
        {
            var path = filepath ?? OriginalFilePath;
            using (var writer = new StreamWriter(path + ".temp", false, Encoding.UTF8))
            {
                rs_serializer.Serialize(writer, this);
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(path + ".temp", path);
        }
    }
}
