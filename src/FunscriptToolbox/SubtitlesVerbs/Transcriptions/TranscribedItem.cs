using FunscriptToolbox.SubtitlesVerbs.Infra;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscribedItem : TimedItemWithMetadata
    {
        public double NoSpeechProbability { get; }
        public TranscribedWord[] Words { get; }

        public TranscribedItem(
            TimeSpan startTime,
            TimeSpan endTime,
            MetadataCollection metadata = null,
            double noSpeechProbability = 0.0,
            IEnumerable<TranscribedWord> words = null,
            DateTime? creationTime = null)
            : base(startTime, endTime, metadata, creationTime)
        {
            NoSpeechProbability = noSpeechProbability;
            Words = words?.ToArray() ?? Array.Empty<TranscribedWord>();
        }
    }
}