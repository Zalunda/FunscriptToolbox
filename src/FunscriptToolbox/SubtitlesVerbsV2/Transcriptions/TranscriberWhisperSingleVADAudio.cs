using FunscriptToolbox.SubtitlesVerbV2;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbsV2.Transcriptions
{
    public class TranscriberWhisperSingleVADAudio : TranscriberWhisper
    {
        public TranscriberWhisperSingleVADAudio()
        {
        }

        public override Transcription Transcribe(
            SubtitleGeneratorContext context,
            FfmpegAudioHelper audioHelper, 
            PcmAudio pcmAudio,
            Language overrideLanguage)
        {
            // TODO Add info/verbose logs

            var transcribedLanguage = overrideLanguage ?? this.Language;
            if (context.Wipsub.SubtitlesForcedTiming == null)
            {
                // TODO Maybe add a PrerequisiteMet method
                return null;
            }

            var audioSections = context
                .Wipsub
                .SubtitlesForcedTiming
                .Where(f => f.Type == SubtitleForcedTimingType.Voice)
                .Select(
                    vad => pcmAudio.ExtractSnippet(vad.StartTime, vad.EndTime))
                .ToArray();
            var transcribedTexts = this.TranscriberTool.TranscribeAudio(
                audioHelper,
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