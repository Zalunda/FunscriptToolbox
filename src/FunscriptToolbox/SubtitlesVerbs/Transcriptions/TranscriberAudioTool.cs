using FunscriptToolbox.SubtitlesVerbs.AudioExtraction;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public abstract class TranscriberAudioTool
    {
        public abstract void TranscribeAudio(
            SubtitleGeneratorContext context,
            Transcription transcription,
            TimedObjectWithMetadata<PcmAudio>[] items);
    }
}