using FunscriptToolbox.SubtitlesVerbV2;

namespace FunscriptToolbox.SubtitlesVerbsV2.Transcriptions
{
    public class TranscriberWhisperFullAudio : TranscriberWhisper
    {
        public TranscriberWhisperFullAudio()
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
            var transcribedTexts = this.TranscriberTool.TranscribeAudio(
                     audioHelper,
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