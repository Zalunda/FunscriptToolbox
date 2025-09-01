using FunscriptToolbox.SubtitlesVerbs.AudioExtractions;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public abstract class TranscriberToolAudio
    {
        public abstract TranscribedItem[] TranscribeAudio(
            SubtitleGeneratorContext context,
            Transcription transcription,
            PcmAudio[] items,
            string metadataProduced);
    }
}