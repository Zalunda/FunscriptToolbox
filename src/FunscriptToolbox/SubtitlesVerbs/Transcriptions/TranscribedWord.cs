﻿using System;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscribedWord
    {
        public TimeSpan StartTime { get; }
        public TimeSpan EndTime { get; }
        public TimeSpan Duration => EndTime - StartTime;
        public string Text { get; }
        public double Probability { get; }

        public TranscribedWord(
            TimeSpan startTime, 
            TimeSpan endTime, 
            string text, 
            double probability)
        {
            StartTime = startTime;
            EndTime = endTime;
            Text = text;
            Probability = probability;
        }
    }
}