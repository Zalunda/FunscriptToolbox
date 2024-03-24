using FunscriptToolbox.SubtitlesVerbsV2.Translations;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbsV2.Transcriptions
{
    public class TranscribedText
    {
        public TimeSpan StartTime { get; }
        public TimeSpan EndTime { get; }
        public TimeSpan Duration => EndTime - StartTime;
        public string Text { get; }

        public double NoSpeechProbability { get; }
        public TranscribedWord[] Words { get; }

        public List<TranslatedText> TranslatedTexts { get; }

        public TranscribedText(
            TimeSpan startTime, 
            TimeSpan endTime, 
            string text, 
            double noSpeechProbability, 
            IEnumerable<TranscribedWord> words, 
            IEnumerable<TranslatedText> translatedTexts = null)
        {
            StartTime = startTime;
            EndTime = endTime;
            Text = text;

            NoSpeechProbability = noSpeechProbability;
            Words = words.ToArray();

            TranslatedTexts = new List<TranslatedText>(
                translatedTexts ?? Array.Empty<TranslatedText>());
        }
    }
}