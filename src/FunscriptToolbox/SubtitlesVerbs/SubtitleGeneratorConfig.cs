using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs
{
    public class SubtitleGeneratorConfig
    {
        /// <summary>
        /// Settings shared by all the scenes processed at the same time (ex. how many ffmpeg at the same time).
        /// Only read once, at the start, from the main configuration file (and the .override.config in the same folder):
        /// an error is reported if a configuration file of a sub-folder has this section.
        /// </summary>
        [JsonProperty(Order = -1)]
        public SubtitleGeneratorGlobalSharedSettings GlobalShared { get; set; }

        /// <summary>Settings that are not tied to a worker (where the files go, etc.).</summary>
        [JsonProperty(Order = 0)]
        public SubtitleGeneratorGlobalSettings Global { get; set; }

        [JsonProperty(Order = 1)]
        public Language SourceLanguage { get; set; } = Language.FromString("ja");

        [JsonProperty(Order = 2)]
        public object[] SharedObjects { get; set; }

        [JsonProperty(Order = 3)]
        public SubtitleWorker[] Workers { get; set; }
    }

    /// <summary>See <see cref="SubtitleGeneratorConfig.GlobalShared"/>.</summary>
    public class SubtitleGeneratorGlobalSharedSettings
    {
        /// <summary>
        /// How many of each kind of work can run at the same time, when several scenes are processed in parallel (--ui).
        /// The key is the short name of the lock shown in the web UI: GPU, VID, AUD, CLIP, PREP (ex. { "CLIP": 16 }).
        /// A lock not listed here keeps its default value. ('USER' is always 1: one prompt / dialog at a time.)
        /// </summary>
        [JsonProperty(Order = 1)]
        public Dictionary<string, int> MaxConcurrent { get; set; }

        /// <summary>
        /// Which scenes get the shared resources first (ffmpeg, GPU, preparation of the AI requests...), from their metadata
        /// (see SubtitleGeneratorGlobalSettings.SceneMetadata): the first rule that matches a scene gives its priority (the first rule = the highest).
        /// A scene that matches no rule comes after all of them. Within the same priority: the order of the scenes in the list.
        /// Only the order in which the scenes waiting for the same resource are served changes: all the scenes still run.
        /// (shared: a scene can't give itself a higher priority with the configuration of its folder)
        /// </summary>
        [JsonProperty(Order = 2)]
        public ScenePriorityRule[] ScenePriorities { get; set; }
    }

    public class SubtitleGeneratorGlobalSettings
    {
        /// <summary>
        /// Where the backup folder of each scene ('&lt;scene&gt;_Backup') is created.
        /// null: next to the video (default). Otherwise: a full path (ex. "D:\\FSTB-Backups"), and the backup folder of a scene
        /// is '&lt;BackupParentFolder&gt;\&lt;scene&gt;_Backup'.
        /// </summary>
        [JsonProperty(Order = 1)]
        public string BackupParentFolder { get; set; }

        /// <summary>
        /// Regular expressions applied to the path of the video / .vseq, relative to the folder of the main configuration file
        /// (ex. "03-REDO\\3DSVR-1072\\3DSVR-1072- - ....vseq").
        /// Their named groups become metadata of the scene (ex. "(?&lt;ShortName&gt;...)"), used by <see cref="SceneDisplayName"/>.
        /// All the patterns are tried, in order: for each metadata, the first pattern that finds it wins (the next ones are fallbacks).
        /// "FileName" (the name without extension) is always available.
        /// </summary>
        [JsonProperty(Order = 2)]
        public string[] SceneMetadata { get; set; }

        /// <summary>
        /// How a scene is named in the web UI, with the metadata between braces, and the optional parts between brackets
        /// (ex. "{ShortName}[ ({Type})]": " (REDO)" is only added when 'Type' is found).
        /// null: the file name. When a required metadata (not in an optional part) is not found for a scene, the file name is shown, with an error.
        /// </summary>
        [JsonProperty(Order = 3)]
        public string SceneDisplayName { get; set; }
    }

    /// <summary>A rule of <see cref="SubtitleGeneratorGlobalSharedSettings.ScenePriorities"/>.</summary>
    public class ScenePriorityRule
    {
        /// <summary>The name of a metadata of the scene (ex. "Type", see SceneMetadata; "FileName" always exists).</summary>
        [JsonProperty(Order = 1, Required = Required.Always)]
        public string Metadata { get; set; }

        /// <summary>A regular expression on its value (not case sensitive), ex. "ManualHQWorkflow-G4" for "02-ManualHQWorkflow-G4".</summary>
        [JsonProperty(Order = 2, Required = Required.Always)]
        public string Pattern { get; set; }
    }
}