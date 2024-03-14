using FunscriptToolbox.SubtitlesVerbsV2.Translation;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbsV2.Transcription
{
    internal class TranscribedText
    {
        public TimeSpan StartTime { get; }
        public TimeSpan EndTime { get; }
        public TimeSpan Duration => EndTime - StartTime;
        public string Text { get; }

        public double NoSpeechProbability { get; }
        public TranscribedWord[] Words { get; }

        public List<TranslatedText> Translations { get; }

        public TranscribedText(
            TimeSpan startTime, 
            TimeSpan endTime, 
            string text, 
            double noSpeechProbability, 
            IEnumerable<TranscribedWord> words, 
            IEnumerable<TranslatedText> translations = null)
        {
            StartTime = startTime;
            EndTime = endTime;
            Text = text;

            NoSpeechProbability = noSpeechProbability;
            Words = words.ToArray();

            Translations = translations == null ? new List<TranslatedText>() : translations.ToList();
        }
    }
}