using FunscriptToolbox.SubtitlesVerbs.AudioExtractions;
using FunscriptToolbox.SubtitlesVerbs.Infra;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public abstract class TranscriberToolAudio
    {
        public abstract TimedItemWithMetadata[] TranscribeAudio(
            SubtitleGeneratorContext context,
            Track transcription,
            PcmAudio[] items,
            string metadataProduced);
    }
}