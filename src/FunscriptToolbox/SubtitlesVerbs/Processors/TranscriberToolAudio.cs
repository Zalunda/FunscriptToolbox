using FunscriptToolbox.SubtitlesVerbs.Inputs;
using FunscriptToolbox.SubtitlesVerbs.Infra;

namespace FunscriptToolbox.SubtitlesVerbs.Processors
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