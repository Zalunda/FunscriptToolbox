using FunscriptToolbox.Core;
using FunscriptToolbox.SubtitlesVerbs.Translations;
using Newtonsoft.Json;
using System.Linq;
using System;
using FunscriptToolbox.SubtitlesVerbs.Infra;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public abstract class Transcriber : SubtitleWorker
    {
        [JsonProperty(Order = 1)]
        public bool Enabled { get; set; } = true;

        [JsonProperty(Order = 2, Required = Required.Always)]
        public string TranscriptionId { get; set; }

        [JsonProperty(Order = 100, TypeNameHandling = TypeNameHandling.None)]
        public Translator[] Translators { get; set; }

        [JsonIgnore]
        public virtual bool CanBeUpdated { get; } = false;

        public Transcriber()
        {
        }

        public abstract bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason);

        public abstract void Transcribe(
            SubtitleGeneratorContext context,
            Transcription transcription);
    }
}