using FunscriptToolbox.SubtitlesVerbs.Translations;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
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
            double noSpeechProbability = 0.0, 
            IEnumerable<TranscribedWord> words = null, 
            IEnumerable<TranslatedText> translatedTexts = null)
        {
            StartTime = startTime;
            EndTime = endTime;
            Text = text;

            NoSpeechProbability = noSpeechProbability;
            Words = words?.ToArray() ?? Array.Empty<TranscribedWord>();

            TranslatedTexts = new List<TranslatedText>(
                translatedTexts ?? Array.Empty<TranslatedText>());
        }

        public string GetFirstTranslatedIfPossible()
        {
            return this.TranslatedTexts.FirstOrDefault()?.Text ?? this.Text;
        }
    }
}