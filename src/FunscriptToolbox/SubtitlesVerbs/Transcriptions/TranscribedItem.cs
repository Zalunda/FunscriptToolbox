using FunscriptToolbox.SubtitlesVerbs.Infra;
using System;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    /// <remarks>
    /// (it had 'Words' and 'NoSpeechProbability' before: the ones of the old .wipsubtitles are ignored when read; a step that needs the
    /// words keeps them in its job state, ex. TranscriberAudioFullAIGoogleTranscribe)
    /// </remarks>
    public class TranscribedItem : TimedItemWithMetadata
    {
        public TranscribedItem(
            TimeSpan startTime,
            TimeSpan endTime,
            MetadataCollection metadata = null,
            DateTime? creationTime = null)
            : base(startTime, endTime, metadata, creationTime)
        {
        }
    }
}