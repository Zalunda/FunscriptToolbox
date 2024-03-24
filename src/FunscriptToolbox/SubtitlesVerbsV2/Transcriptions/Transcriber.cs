using FunscriptToolbox.SubtitlesVerbsV2.Translations;
using FunscriptToolbox.SubtitlesVerbV2;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbsV2.Transcriptions
{
    public abstract class Transcriber
    {
        public string TranscriptionId { get; }
        public Language Language { get; set; } = Language.FromString("ja");

        [JsonProperty(TypeNameHandling = TypeNameHandling.None)]
        public Translator[] Translators { get; }

        public Transcriber(
            string transcriptionId,
            IEnumerable<Translator> translators)
        {
            this.TranscriptionId = transcriptionId;
            this.Translators = translators.ToArray();
        }

        public abstract Transcription Transcribe(
            FfmpegAudioHelper ffmpegAudioHelper,
            PcmAudio pcmAudio,
            IEnumerable<SubtitleForcedLocation> subtitlesForcedLocation,
            Language overrideLanguage);
    }
}