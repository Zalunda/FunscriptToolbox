using FunscriptToolbox.SubtitlesVerbsV2.AudioExtraction;
using Newtonsoft.Json;

namespace FunscriptToolbox.SubtitlesVerbsV2.Transcriptions
{
    public abstract class TranscriberTool
    {
        [JsonProperty(Order = 1, Required = Required.Always)]
        public string ApplicationFullPath { get; set; }
        [JsonProperty(Order = 2)]
        public string AdditionalParameters { get; set; } = "";

        public abstract TranscribedText[] TranscribeAudio(
            SubtitleGeneratorContext context,
            ProgressUpdateDelegate progressUpdateCallback,
            PcmAudio[] audios,
            Language sourceLanguage,
            string filesPrefix,
            out TranscriptionCost[] costs);
    }
}