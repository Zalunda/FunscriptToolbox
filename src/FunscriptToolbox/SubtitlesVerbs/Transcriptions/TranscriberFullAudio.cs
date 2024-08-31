using FunscriptToolbox.SubtitlesVerbs.AudioExtraction;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscriberFullAudio : Transcriber
    {
        public TranscriberFullAudio()
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