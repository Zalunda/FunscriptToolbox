using FunscriptToolbox.SubtitlesVerbsV2.AudioExtraction;

namespace FunscriptToolbox.SubtitlesVerbsV2.Transcriptions
{
    public class TranscriberWhisperFullAudio : TranscriberWhisper
    {
        public TranscriberWhisperFullAudio()
        {
        }

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