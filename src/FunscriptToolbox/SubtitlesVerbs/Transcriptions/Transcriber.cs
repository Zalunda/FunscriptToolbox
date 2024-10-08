﻿using FunscriptToolbox.SubtitlesVerbs.AudioExtraction;
using FunscriptToolbox.SubtitlesVerbs.Translations;
using Newtonsoft.Json;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public abstract class Transcriber
    {
        [JsonProperty(Order = 1)]
        public bool Enabled { get; set; } = true;

        [JsonProperty(Order = 2, Required = Required.Always)]
        public string TranscriptionId { get; set; }

        [JsonProperty(Order = 3)]
        public Language Language { get; set; } = null;

        [JsonProperty(Order = 4, Required = Required.Always)]
        public TranscriberTool TranscriberTool { get; set; }


        [JsonProperty(Order = 100, TypeNameHandling = TypeNameHandling.None)]
        public Translator[] Translators { get; set; }

        public Transcriber()
        {
        }

        public abstract bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason);

        public abstract Transcription Transcribe(
            SubtitleGeneratorContext context,
            PcmAudio pcmAudio,
            Language overrideLanguage);
    }
}