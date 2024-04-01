using FunscriptToolbox.SubtitlesVerbV2;
using Newtonsoft.Json;

namespace FunscriptToolbox.SubtitlesVerbsV2.Transcriptions
{
    public abstract class TranscriberTool
    {
        [JsonProperty(Order = 1)]
        public string ApplicationFullPath { get; set; }
        [JsonProperty(Order = 2)]
        public string AdditionalParameters { get; set; } = "";

        public abstract TranscribedText[] TranscribeAudio(
            FfmpegAudioHelper audioHelper,
            PcmAudio[] audios,
            Language sourceLanguage,
            out TranscriptionCost[] costs);
    }
}