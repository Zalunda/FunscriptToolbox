﻿using FunscriptToolbox.SubtitlesVerbs.AudioExtraction;
using Newtonsoft.Json;

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
            var transcribedTexts = this.TranscriberTool.TranscribeAudio(
                     context,
                     context.DefaultProgressUpdateHandler,
                     new[] { pcmAudio },
                     transcribedLanguage,
                     $"{this.TranscriptionId}-",
                     out var costs);
            return new Transcription(
                this.TranscriptionId,
                transcribedLanguage,
                transcribedTexts, 
                costs);
        }
    }
}