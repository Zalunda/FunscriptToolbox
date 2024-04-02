using FunscriptToolbox.SubtitlesVerbV2;

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
            FfmpegAudioHelper audioHelper,
            PcmAudio pcmAudio,
            Language overrideLanguage)
        {
            var transcribedLanguage = overrideLanguage ?? this.Language;
            var transcribedTexts = this.TranscriberTool.TranscribeAudio(
                     audioHelper,
                     context.DefaultProgressUpdateHandler,
                     new[] { pcmAudio },
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