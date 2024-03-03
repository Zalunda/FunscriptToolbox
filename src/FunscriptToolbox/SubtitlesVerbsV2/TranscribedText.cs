using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbV2
{
    internal class TranscribedText
    {
        public TimeSpan StartTime { get; }
        public TimeSpan EndTime { get; }
        public TimeSpan Duration => EndTime - StartTime;
        public string Text { get; }

        public double NoSpeechProbability { get; }
        public TranscribedWord[] Words { get; }

        public string Translation { get; set; } // TODO Remove this

        public TranscribedText(TimeSpan startTime, TimeSpan endTime, string text, double noSpeechProbability, IEnumerable<TranscribedWord> words, string translation = null)
        {
            StartTime = startTime;
            EndTime = endTime;
            Text = text;

            NoSpeechProbability = noSpeechProbability;
            Words = words.ToArray();

            Translation = translation;
        }
    }
}