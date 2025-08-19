using FunscriptToolbox.SubtitlesVerbs.AudioExtraction;
using Newtonsoft.Json;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscriberFullAudio : Transcriber
    {
        public TranscriberFullAudio()
        {
        }

        [JsonProperty(Order = 20, Required = Required.Always)]
        public TranscriberTool TranscriberTool { get; set; }

        public override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            IEnumerable<Transcriber> transcribers,
            out string reason)
        {
            reason = null;
            return true;
        }

        public override Transcription Transcribe(
            SubtitleGeneratorContext context,
            PcmAudio pcmAudio,
            Language overrideLanguage)
        {
            var transcribedLanguage = overrideLanguage ?? this.Language;
            var transcription = new Transcription(
                this.TranscriptionId,
                transcribedLanguage);
            this.TranscriberTool.TranscribeAudio(
                     context,
                     context.DefaultProgressUpdateHandler,
                     transcription,
                     new[] { pcmAudio },
                     $"{this.TranscriptionId}-");
            return transcription;
        }
    }
}