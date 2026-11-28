using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.Inputs;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using FunscriptToolbox.SubtitlesVerbs.Processors;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace FunscriptToolbox.SubtitlesVerbs
{
    [JsonObject(IsReference = false)]
    public class WorkInProgressSubtitles
    {
        /// <remarks>
        /// 3.0: one list of tracks ('Tracks') instead of 'Transcriptions' and 'Translations' (a 2.x file is read into 'Tracks', and
        /// saved in 3.0: a version before 3.3 can't open it anymore).
        /// </remarks>
        public const string CURRENT_FORMAT_VERSION = "3.0";
        public const string BACKUP_FOLDER_SUFFIX = "_Backup";

        /// <summary>
        /// The serializer of the .wipsubtitles, one by thread: a JsonSerializer that preserves the references is NOT thread safe (its
        /// reference resolver numbers the objects with a counter shared by all its calls: two scenes saving at the same time => the same
        /// number twice, "Error writing object reference for '...TranscribedItem'", the file not saved).
        /// </summary>
        private static JsonSerializer rs_serializer => rs_serializerByThread.Value;

        private static readonly System.Threading.ThreadLocal<JsonSerializer> rs_serializerByThread = new System.Threading.ThreadLocal<JsonSerializer>(() =>
        {
            var serializer = JsonSerializer.Create(new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                PreserveReferencesHandling = PreserveReferencesHandling.Objects
            });
            serializer.Converters.Add(new StringEnumConverter());
            return serializer;
        });
        public const string Extension = ".wipsubtitles";

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

                // (one list now: an id can't be used twice, ex. a transcription and a translation with the same id in a 2.x file)
                var duplicates = content.Tracks.GroupBy(t => t.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
                if (duplicates.Length > 0)
                {
                    throw new Exception(
                        $"File '{Path.GetFileName(fullpath)}': these ids are used by more than one track: {string.Join(", ", duplicates.Select(d => $"'{d}'"))} " +
                        "(ex. a transcription and a translation with the same id, before 3.3). Rename one of them in the file (ex. '<id>-OLD').");
                }

                // (read in the current format, ex. the 2 lists of a 2.x file merged in 'Tracks': saved in the current format)
                content.UpdateFormatVersion();
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
            this.Tracks = new List<Track>();
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
            this.BackupFolder = $"{BaseFilePath}{BACKUP_FOLDER_SUFFIX}";
        }

        /// <summary>
        /// Moves the backup folder under another parent folder (see SubtitleGeneratorGlobalSettings.BackupParentFolder).
        /// null: the backup folder stays next to the video.
        /// </summary>
        public void SetBackupParentFolder(string backupParentFolder)
        {
            if (backupParentFolder == null)
            {
                this.BackupFolder = $"{BaseFilePath}{BACKUP_FOLDER_SUFFIX}";
                return;
            }

            if (!Path.IsPathFullyQualified(backupParentFolder))
            {
                throw new Exception($"The configuration 'Global.BackupParentFolder' must be a full path (ex. \"D:\\\\FSTB-Backups\"), received: '{backupParentFolder}'.");
            }

            this.BackupFolder = Path.Combine(
                backupParentFolder,
                $"{Path.GetFileName(BaseFilePath)}{BACKUP_FOLDER_SUFFIX}");
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

        /// <summary>
        /// 'Pause scene' in the web UI, remembered: the scene pauses at its first safe point each time it's loaded (also after a restart
        /// of the application), until 'Resume scene'. (not written when false)
        /// </summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool IsOnHold { get; set; }

        public TimelineMap TimelineMap { get; set; }
        public List<AudioExtraction> AudioExtractions { get; set; }

        /// <summary>What the steps produced (transcriptions, translations...), each under the id of its step.</summary>
        public List<Track> Tracks { get; set; }

        /// <summary>(read only: the 2 lists of the format 2.x, added to Tracks; the transcriptions first, like before)</summary>
        [JsonProperty("Transcriptions")]
        private List<Track> LegacyTranscriptions { set => this.Tracks.AddRange(value ?? new List<Track>()); }

        /// <summary>(read only, see LegacyTranscriptions)</summary>
        [JsonProperty("Translations")]
        private List<Track> LegacyTranslations { set => this.Tracks.AddRange(value ?? new List<Track>()); }

        public VirtualSubtitleFile CreateVirtualSubtitleFile()
        {
            return new VirtualSubtitleFile(this.TimelineMap);
        }

        public VirtualSubtitleFile LoadVirtualSubtitleFile(string fileSuffix)
        {
            return VirtualSubtitleFile.Load(this.TimelineMap, this.ParentPath, fileSuffix);
        }


        [JsonIgnore]
        public IEnumerable<TimedItemWithMetadataCollection> WorkersResult => (IEnumerable<TimedItemWithMetadataCollection>)this.Tracks ?? Array.Empty<TimedItemWithMetadataCollection>();

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

        /// <summary>
        /// The ids of the steps renamed in 3.3 (old => the new ids it can have: a workflow has only one of them, ex. 'singlevad-ai-refined'
        /// translates in ManualHQWorkflow-G4, it doesn't in the older workflows).
        /// </summary>
        private static readonly Dictionary<string, string[]> rs_renamedTrackIds = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["full-whisper"] = new[] { "transcription-from-whisper" },
            ["full-whisper_google"] = new[] { "quick-translation-of-whisper-using-google" },
            ["full-whisper_local-api"] = new[] { "quick-translation-of-whisper-using-local-api" },
            ["full-ai"] = new[] { "transcription-from-ai" },
            ["full-transcribe"] = new[] { "transcription-from-google-ai-transcribe" },
            ["full-ai_google"] = new[] { "quick-translation-of-ai-using-google" },
            ["manual-input-preparer"] = new[] { "annotated-timings-proposal" },
            ["manual-input-review"] = new[] { "annotated-timings-review-in-editor" },
            ["manual-input"] = new[] { "annotated-timings" },
            ["singlevad-ai-refined"] = new[] { "analysis-and-translation-en", "analysis-and-transcription" },
            ["translated-texts_maverick"] = new[] { "translation-en" },
            ["finalized_maverick"] = new[] { "final-subtitles-en" },
            ["translated-texts_french"] = new[] { "translation-fr" },
            ["finalized_french"] = new[] { "final-subtitles-fr" },
        };

        /// <summary>
        /// The tracks of the steps renamed in 3.3: under their new id (the work done is kept). An old id with several new ids: the one of
        /// the configuration of the scene (enabled first). Not when the configuration has no step with the new id, or a track already has
        /// it. The 'SourceIdUsed' of a clone too (otherwise it's done again). Returns the ids renamed ("old => new").
        /// </summary>
        public List<string> RenameOldTrackIds(IEnumerable<SubtitleWorker> workers)
        {
            var workerList = workers.ToList();
            string NewId(string oldId)
            {
                if (oldId == null || !rs_renamedTrackIds.TryGetValue(oldId, out var candidates))
                    return null;
                return candidates.FirstOrDefault(c => workerList.Any(w => w.Enabled && w.Id == c))
                    ?? candidates.FirstOrDefault(c => workerList.Any(w => w.Id == c));
            }

            var renamed = new List<string>();
            foreach (var track in this.Tracks.ToList())
            {
                var newId = NewId(track.Id);
                if (newId != null && !this.Tracks.Any(t => t.Id == newId))
                {
                    renamed.Add($"{track.Id} => {newId}");
                    track.ChangeId(newId);
                }
            }
            foreach (var clone in this.Tracks.Select(t => t.CurrentJobState).OfType<TransformClone.JobState>())
            {
                var newId = NewId(clone.SourceIdUsed);
                if (newId != null)
                    clone.SourceIdUsed = newId;
            }
            return renamed;
        }

        private readonly object r_saveLock = new object();

        /// <summary>
        /// Runs 'action' while no save is in progress. A thread that changes data that is saved (while another thread may call Save)
        /// must do it in here, otherwise the save can see the data half changed.
        /// </summary>
        public void ExecuteLocked(Action action)
        {
            lock (r_saveLock)
            {
                action();
            }
        }

        /// <summary>Thread safe: the saves (and the changes made with <see cref="ExecuteLocked"/>) wait for each other.</summary>
        public void Save(string filepath = null)
        {
            lock (r_saveLock)
            {
                var path = filepath ?? OriginalFilePath;
                using (var writer = new StreamWriter(path + ".temp", false, Encoding.UTF8))
                {
                    rs_serializer.Serialize(writer, this);
                }

                // (replaced in one move: never a moment without the file, ex. while another thread loads it)
                try
                {
                    File.Move(path + ".temp", path, overwrite: true);
                }
                catch
                {
                    Thread.Sleep(TimeSpan.FromSeconds(2));
                    File.Move(path + ".temp", path, overwrite: true);
                }
            }
        }
    }
}
