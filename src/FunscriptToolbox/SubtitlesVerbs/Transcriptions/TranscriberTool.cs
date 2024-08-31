using FunscriptToolbox.SubtitlesVerbs.AudioExtraction;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public abstract class TranscriberTool
    {
        public abstract TranscribedText[] TranscribeAudio(
            SubtitleGeneratorContext context,
            ProgressUpdateDelegate progressUpdateCallback,
            PcmAudio[] audios,
            Language sourceLanguage,
            string filesPrefix,
            out TranscriptionCost[] costs);
    }
}