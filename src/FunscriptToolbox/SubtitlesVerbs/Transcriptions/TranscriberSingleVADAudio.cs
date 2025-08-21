using FunscriptToolbox.SubtitlesVerbs.AudioExtraction;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscriberSingleVADAudio : Transcriber
    {
        public TranscriberSingleVADAudio()
        {
        }


        [JsonProperty(Order = 20, Required = Required.Always)]
        public TranscriberTool TranscriberTool { get; set; }
        [JsonProperty(Order = 21)]
        public string UseTimingsFromId { get; set; } = null;
        [JsonProperty(Order = 22)]
        public TimeSpan ExpandStart { get; set; } = TimeSpan.Zero;
        [JsonProperty(Order = 23)]
        public TimeSpan ExpandEnd { get; set; } = TimeSpan.Zero;

        public override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            IEnumerable<Transcriber> transcribers,
            out string reason)
        {
            if (this.UseTimingsFromId != null && !context.CurrentWipsub.Transcriptions.Any(f => f.Id == this.UseTimingsFromId))
            {
                reason = $"Transcription '{this.UseTimingsFromId}' not done yet.";
                return false;
            }
            else
            {
                reason = "SubtitlesForcedTiming not imported yet.";
                return context.CurrentWipsub.SubtitlesForcedTiming != null;
            }
        }

        public override void Transcribe(
            SubtitleGeneratorContext context,
            Transcription transcription,
            PcmAudio pcmAudio,
            Language overrideLanguage)
        {
            var transcribedLanguage = overrideLanguage ?? this.Language;
            var timings = UseTimingsFromId == null
                ? context.CurrentWipsub.SubtitlesForcedTiming.Where(f => f.VoiceText != null).Cast<ITiming>().ToArray()
                : context.CurrentWipsub.Transcriptions.FirstOrDefault(f => f.Id == this.UseTimingsFromId).Items.Cast<ITiming>().ToArray();
            var audioSections = timings
                .Select(
                    vad => pcmAudio.ExtractSnippet(vad.StartTime - this.ExpandStart, vad.EndTime + this.ExpandEnd))
                .ToArray();
            this.TranscriberTool.TranscribeAudio(
                context,
                context.DefaultProgressUpdateHandler,
                transcription,
                audioSections,
                $"{this.TranscriptionId}-");
            transcription.MarkAsFinished();
        }
    }
}