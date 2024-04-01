using FunscriptToolbox.SubtitlesVerbsV2.Translations;
using FunscriptToolbox.SubtitlesVerbV2;
using Newtonsoft.Json;

namespace FunscriptToolbox.SubtitlesVerbsV2.Transcriptions
{
    public abstract class Transcriber
    {
        [JsonProperty(Order = 1)]
        public bool Enabled { get; set; } = true;

        [JsonProperty(Order = 2)]
        public string TranscriptionId { get; set; }

        [JsonProperty(Order = 3)]
        public Language Language { get; set; } = null;

        [JsonProperty(Order = 100, TypeNameHandling = TypeNameHandling.None)]
        public Translator[] Translators { get; set; }

        public Transcriber()
        {
        }

        public abstract Transcription Transcribe(
            SubtitleGeneratorContext context,
            FfmpegAudioHelper ffmpegAudioHelper,
            PcmAudio pcmAudio,
            Language overrideLanguage);
    }
}