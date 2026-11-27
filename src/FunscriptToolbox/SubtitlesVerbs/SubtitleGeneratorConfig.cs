using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs
{
    public class SubtitleGeneratorConfig
    {
        [ConfigDoc("Settings shared by all the scenes processed at the same time (--ui): how many of each kind of work can run at the same time, which scenes are served first.\n" +
            "Only read at the start, from the main folder (its .config, .override.config, .override.user.config): an error is reported if a configuration file of a sub-folder has this section.",
            Advice = "A change is used after a restart of the application.")]
        [JsonProperty(Order = -1)]
        public SubtitleGeneratorGlobalSharedSettings GlobalShared { get; set; }

        [ConfigDoc("Settings that are not tied to a step: where the backup folders go, how the scenes are named in the web UI.")]
        [JsonProperty(Order = 0)]
        public SubtitleGeneratorGlobalSettings Global { get; set; }

        [ConfigDoc("The language spoken in the videos (ex. \"ja\", \"en\"): used in the prompts ([TranscriptionLanguage]) and by the transcribers.\n" +
            "A folder or a video file with a language code between brackets (ex. 'Scene [es].mp4') uses that language instead.")]
        [JsonProperty(Order = 1)]
        public Language SourceLanguage { get; set; } = Language.FromString("ja");

        [ConfigDoc("The objects that can be used by several steps, each with an '$id' (AI engines, prompts, timing refiner engines...). A step uses one with { \"$ref\": \"<id>\" }.\n" +
            "An override file can change a value of a shared object by repeating its '$id' with only that value.",
            Advice = "To change a value for one step only, use ✎ on it in the step's Config tab and choose 'this step only'.")]
        [JsonProperty(Order = 2)]
        public object[] SharedObjects { get; set; }

        [ConfigDoc("The steps of the workflow, in order: audio extractions, transcriptions, translations, outputs. Each step uses what the steps before it produced.\n" +
            "An override file changes a step by repeating its id (ex. \"Id\": \"full-ai\") with only the values it changes.")]
        [JsonProperty(Order = 3)]
        public SubtitleWorker[] Workers { get; set; }

        /// <summary>
        /// A hash of the merged configuration, when it was loaded: the 'Config' tab of the web UI reads the files again, and says when
        /// they changed since the scene loaded its configuration (see SubtitleGeneratorConfigLoader.LoadHierarchically).
        /// </summary>
        [JsonIgnore]
        internal string MergedConfigHash { get; set; }
    }

    /// <summary>See <see cref="SubtitleGeneratorConfig.GlobalShared"/>.</summary>
    public class SubtitleGeneratorGlobalSharedSettings
    {
        [ConfigDoc("How many of each kind of work can run at the same time, all the scenes together (the short names shown in the web UI):\n" +
            "  GPU = the local machine (Purfview whisper, local AI), VID = ffmpeg reading the videos, AUD = ffmpeg on a whole audio,\n" +
            "  PREP = preparation of the AI requests, ENC = ffmpeg creating a whole video (ex. '.FAST.mp4').\n" +
            "A kind not listed keeps its default value. ('USER' is always 1: one prompt / dialog at a time.)",
            Advice = "Keep GPU at 1 unless your GPU has the memory for two models. Raise AUD / VID if your disk and CPU keep up (more scenes progress at the same time).")]
        [JsonProperty(Order = 1)]
        public Dictionary<string, int> MaxConcurrent { get; set; }

        [ConfigDoc("Which scenes get the shared resources first (ffmpeg, GPU, preparation of the AI requests...), from their metadata (see Global.SceneMetadata).\n" +
            "The first rule that matches a scene gives its priority (the first rule = the highest); a scene that matches no rule comes after all of them. " +
            "Within the same priority: the order of the scenes in the list.\n" +
            "Only who is served first changes: all the scenes still run. (shown in the tooltip of a scene in the web UI; in GlobalShared: a scene can't give itself a higher priority with the configuration of its folder)",
            Advice = "Put first the workflows where you wait for the result (ex. the one you review next).")]
        [JsonProperty(Order = 2)]
        public ScenePriorityRule[] ScenePriorities { get; set; }
    }

    public class SubtitleGeneratorGlobalSettings
    {
        [ConfigDoc("Where the backup folder of each scene ('<scene>_Backup': old versions of the files, AI requests / responses...) is created.\n" +
            "null: next to the video. Otherwise: a full path (ex. \"D:\\\\FSTB-Backups\", with '\\' doubled in the file), and the backup folder of a scene is '<BackupParentFolder>\\<scene>_Backup'.",
            Advice = "Set it to keep the folders of the videos clean (ex. a folder on another disk).")]
        [JsonProperty(Order = 1)]
        public string BackupParentFolder { get; set; }

        [ConfigDoc("Regular expressions on the path of the video / .vseq, relative to the folder of the main configuration (ex. 'ManualHQWorkflow-G4\\3DSVR-1072\\3DSVR-1072- - ....vseq'). " +
            "Their named groups '(?<Name>...)' become metadata of the scene, used by SceneDisplayName and GlobalShared.ScenePriorities.\n" +
            "All the patterns are tried, in order: for each metadata, the first pattern that finds it wins (the next ones are fallbacks). 'FileName' (the name without extension) always exists.",
            Advice = "In the file, '\\' must be doubled ('\\\\'). Test a new pattern on a few scenes: the web UI shows the file name (with an error) when a required metadata is not found.")]
        [JsonProperty(Order = 2)]
        public string[] SceneMetadata { get; set; }

        [ConfigDoc("How a scene is named in the web UI: the metadata (see SceneMetadata) between braces, the optional parts between brackets " +
            "(ex. \"{ShortName}[ ({Type})]\": ' (Type)' is only added when 'Type' is found).\n" +
            "null: the file name. When a required metadata (not in an optional part) is not found for a scene, its file name is shown, with an error.")]
        [JsonProperty(Order = 3)]
        public string SceneDisplayName { get; set; }
    }

    /// <summary>A rule of <see cref="SubtitleGeneratorGlobalSharedSettings.ScenePriorities"/>.</summary>
    public class ScenePriorityRule
    {
        [ConfigDoc("The name of a metadata of the scene (ex. \"Type\", see Global.SceneMetadata; \"FileName\" always exists).")]
        [JsonProperty(Order = 1, Required = Required.Always)]
        public string Metadata { get; set; }

        [ConfigDoc("A regular expression on the value of the metadata (not case sensitive), ex. \"ManualHQWorkflow-G4\" matches \"02-ManualHQWorkflow-G4\".")]
        [JsonProperty(Order = 2, Required = Required.Always)]
        public string Pattern { get; set; }
    }
}