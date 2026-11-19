using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace FunscriptToolbox.SubtitlesVerbs
{
    public class SubtitleGeneratorConfig
    {
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

    public class SubtitleGeneratorGlobalSettings
    {
        /// <summary>
        /// Where the backup folder of each scene ('&lt;scene&gt;_Backup') is created.
        /// null: next to the video (default). Otherwise: a full path (ex. "D:\\FSTB-Backups"), and the backup folder of a scene
        /// is '&lt;BackupParentFolder&gt;\&lt;scene&gt;_Backup'.
        /// </summary>
        [JsonProperty(Order = 1)]
        public string BackupParentFolder { get; set; }
    }
}