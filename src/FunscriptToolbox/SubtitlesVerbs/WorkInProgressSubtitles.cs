using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.AudioExtractions;
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
                content.SetPaths(filepath, videoPath);
                return content;
            }
            catch (Exception ex)
            {
                if (File.Exists(filepath) && File.ReadAllText(filepath).Contains("\"FormatVersion\": \"1."))
                {
                    throw new Exception($"File format for '{Path.GetFileName(filepath)}' has changed. Need to delete or rename the file and start from the start.", ex);
                }
                ex.Data.Add("File", filepath);
                throw new Exception($"Error parsing file '{filepath}': {ex.Message}", ex);
            }
        }

        public WorkInProgressSubtitles()
        {
            this.AudioExtractions = new List<AudioExtraction>();
            this.Transcriptions = new List<Transcription>();
            this.Translations = new List<Translation>();
        }

        public WorkInProgressSubtitles(string fullpath, string videoPath)
            : this()
        {
            SetPaths(fullpath, videoPath);
        }

        private void SetPaths(string fullpath, string videoPath)
        {
            this.OriginalFilePath = fullpath;
            this.OriginalVideoPath = videoPath;
            this.BaseFilePath = Path.Combine(
                PathExtension.SafeGetDirectoryName(this.OriginalFilePath),
                Path.GetFileNameWithoutExtension(this.OriginalFilePath));
            this.BackupFolder = $"{BaseFilePath}_Backup";
        }

        [JsonIgnore]
        public string OriginalFilePath { get; private set; }
        [JsonIgnore]
        public string OriginalVideoPath { get; private set; }
        public string BaseFilePath { get; private set; }
        public string BackupFolder { get; private set; }

        public string FormatVersion { get; set; } = CURRENT_FORMAT_VERSION;
        public List<AudioExtraction> AudioExtractions { get; set; }
        public List<Transcription> Transcriptions { get; set; }
        public List<Translation> Translations { get; set; }
        public IEnumerable<TimedItemWithMetadataCollection> WorkersResult => ((IEnumerable<TimedItemWithMetadataCollection>)this.Transcriptions ?? Array.Empty<TimedItemWithMetadataCollection>())
            .Union((IEnumerable<TimedItemWithMetadataCollection>)this.Translations ?? Array.Empty<TimedItemWithMetadataCollection>());

        public TimeSpan GetVideoDuration()
        {
            return this.AudioExtractions.FirstOrDefault().PcmAudio.Duration;
        }

        public void FinalizeLoad()
        {
            foreach (var audioFile in this.AudioExtractions)
            {
                audioFile.FinalizeLoad(this.BaseFilePath);
            }
        }

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
