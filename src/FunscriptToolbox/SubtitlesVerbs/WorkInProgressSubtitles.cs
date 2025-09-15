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

        public static WorkInProgressSubtitles FromFile(string fullpath)
        {
            var fileContent = File.ReadAllText(fullpath);
            if (fileContent.IndexOf("\"FormatVersion\": \"1.", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                throw new Exception($"File format for '{Path.GetFileName(fullpath)}' has changed. You need to delete or rename the file and start from the start.");
            }

            try
            {
                using var jsonReader = new JsonTextReader(new StringReader(fileContent));
                var content = rs_serializer.Deserialize<WorkInProgressSubtitles>(jsonReader);
                content.SetPaths(fullpath);
                return content;
            }
            catch (JsonException ex)
            {
                ex.Data.Add("File", fullpath);
                throw new Exception($"Error parsing file '{fullpath}': {ex.Message}", ex);
            }
        }

        [JsonConstructor]
        public WorkInProgressSubtitles()
        {
            this.AudioExtractions = new List<AudioExtraction>();
            this.Transcriptions = new List<Transcription>();
            this.Translations = new List<Translation>();
        }

        public WorkInProgressSubtitles(string fullpath, string[] videoPaths)
            : this()
        {
            SetPaths(fullpath);
            this.TimelineMap = new TimelineMap(videoPaths);
        }

        private void SetPaths(string fullpath)
        {
            this.OriginalFilePath = fullpath;
            this.ParentPath = PathExtension.SafeGetDirectoryName(this.OriginalFilePath);
            this.BaseFilePath = Path.Combine(
                this.ParentPath,
                Path.GetFileNameWithoutExtension(this.OriginalFilePath));
            this.BackupFolder = $"{BaseFilePath}_Backup";
        }

        [JsonIgnore]
        public string OriginalFilePath { get; private set; }
        [JsonIgnore]
        public string BaseFilePath { get; private set; }
        [JsonIgnore]
        public string ParentPath { get; private set; }
        [JsonIgnore]
        public string BackupFolder { get; private set; }

        public string FormatVersion { get; set; } = CURRENT_FORMAT_VERSION;

        public TimelineMap TimelineMap { get; set; }
        public List<AudioExtraction> AudioExtractions { get; set; }
        public List<Transcription> Transcriptions { get; set; }
        public List<Translation> Translations { get; set; }

        public VirtualSubtitleFile CreateVirtualSubtitleFile()
        {
            return new VirtualSubtitleFile(this.TimelineMap);
        }

        public VirtualSubtitleFile LoadVirtualSubtitleFile(string fileSuffix)
        {
            return VirtualSubtitleFile.Load(this.TimelineMap, this.ParentPath, fileSuffix);
        }


        [JsonIgnore]
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
