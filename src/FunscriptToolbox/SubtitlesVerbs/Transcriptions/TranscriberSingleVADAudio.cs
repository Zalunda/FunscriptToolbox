using FunscriptToolbox.SubtitlesVerbs.AudioExtraction;
using System;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscriberSingleVADAudio : Transcriber
    {
        public TranscriberSingleVADAudio()
        {
        }

        public TimeSpan ExpandStart { get; set; } = TimeSpan.Zero;
        public TimeSpan ExpandEnd { get; set; } = TimeSpan.Zero;

        public override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            reason = "SubtitlesForcedTiming not imported yet.";
            return context.CurrentWipsub.SubtitlesForcedTiming != null;
        }

        public override Transcription Transcribe(
            SubtitleGeneratorContext context,
            PcmAudio pcmAudio,
            Language overrideLanguage)
        {
            var transcribedLanguage = overrideLanguage ?? this.Language;
            var audioSections = context
                .CurrentWipsub
                .SubtitlesForcedTiming
                .Where(f => f.VoiceText != null)
                .Select(
                    vad => pcmAudio.ExtractSnippet(vad.StartTime - this.ExpandStart, vad.EndTime + this.ExpandEnd))
                .ToArray();
            var transcribedTexts = this.TranscriberTool.TranscribeAudio(
                context,
                context.DefaultProgressUpdateHandler,
                audioSections,
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