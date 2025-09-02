using FunscriptToolbox.SubtitlesVerbs.Infra;
using System;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscribedWord : ITiming
    {
        public TimeSpan StartTime { get; private set; }
        public TimeSpan EndTime { get; private set; }
        public TimeSpan Duration => EndTime - StartTime;
        public string Text { get; }
        public double Probability { get; }

        public TranscribedWord(
            TimeSpan startTime, 
            TimeSpan endTime, 
            string text, 
            double probability = 0.0)
        {
            StartTime = startTime;
            EndTime = endTime;
            Text = text;
            Probability = probability;
        }

        public void FixTiming(TimeSpan newStartTime, TimeSpan newEndTime)
        {
            this.StartTime = newStartTime;
            this.EndTime = newEndTime;
        }
    }
}