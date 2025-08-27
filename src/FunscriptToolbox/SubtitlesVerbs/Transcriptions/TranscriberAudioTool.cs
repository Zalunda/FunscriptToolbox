using FunscriptToolbox.SubtitlesVerbs.AudioExtraction;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public abstract class TranscriberAudioTool
    {
        public abstract TranscribedItem[] TranscribeAudio(
            SubtitleGeneratorContext context,
            Transcription transcription,
            PcmAudio[] items);
    }
}