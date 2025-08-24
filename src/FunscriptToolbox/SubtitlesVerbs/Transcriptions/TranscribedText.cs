using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscribedText : TimedObjectWithMetadata, ITiming
    {
        public string Text => this.Metadata.Get("VoiceText");
        public double NoSpeechProbability { get; }
        public TranscribedWord[] Words { get; }

        public TranscribedText(
            TimeSpan startTime,
            TimeSpan endTime,
            string text = null,
            MetadataCollection metadata = null,
            double noSpeechProbability = 0.0,
            IEnumerable<TranscribedWord> words = null)
            : base(startTime, endTime, metadata)
        {
            if (!this.Metadata.ContainsKey("VoiceText") && text != null)
            {
                this.Metadata["VoiceText"] = text;
            }
            NoSpeechProbability = noSpeechProbability;
            Words = words?.ToArray() ?? Array.Empty<TranscribedWord>();
        }
    }
}