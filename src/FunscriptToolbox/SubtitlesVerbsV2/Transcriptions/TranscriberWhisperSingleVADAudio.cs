using FunscriptToolbox.SubtitlesVerbV2;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbsV2.Transcriptions
{
    public class TranscriberWhisperSingleVADAudio : TranscriberWhisper
    {
        public TranscriberWhisperSingleVADAudio()
        {
        }

        public override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            reason = "SubtitlesForcedTiming not imported yet";
            return context.Wipsub.SubtitlesForcedTiming != null;
        }

        public override Transcription Transcribe(
            SubtitleGeneratorContext context,
            FfmpegAudioHelper audioHelper, 
            PcmAudio pcmAudio,
            Language overrideLanguage)
        {
            var transcribedLanguage = overrideLanguage ?? this.Language;
            var audioSections = context
                .Wipsub
                .SubtitlesForcedTiming
                .Where(f => f.Type == SubtitleForcedTimingType.Voice)
                .Select(
                    vad => pcmAudio.ExtractSnippet(vad.StartTime, vad.EndTime))
                .ToArray();
            var transcribedTexts = this.TranscriberTool.TranscribeAudio(
                audioHelper,
                context.DefaultProgressUpdateHandler,
                audioSections,
                transcribedLanguage,
                out var costs);
            return new Transcription(
                this.TranscriptionId,
                transcribedLanguage,
                transcribedTexts, 
                costs);
        }
    }
}