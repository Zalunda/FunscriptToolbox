using System;
using System.Collections.Generic;
using System.Linq;
using FunscriptToolbox.SubtitlesVerbs.Transcriptions;

namespace FunscriptToolbox.SubtitlesVerbs.Translations
{
    public class AIRequestForTranslation : AIRequest
    {
        private readonly Translation _translation;
        private readonly Transcription _transcription;
        public TranscribedText[] Items { get; }

        public AIRequestForTranslation(
            string taskId,
            string toolAction,
            int requestNumber,
            List<dynamic> messages,
            Transcription transcription,
            Translation translation,
            TranscribedText[] items)
            : base(
                taskId,
                toolAction,
                requestNumber,
                messages)
        {
            _translation = translation;
            _transcription = transcription;
            Items = items;
        }

        public override void HandleResponse(
            SubtitleGeneratorContext context,
            string taskName,
            TimeSpan timeTaken,
            string responseReceived,
            int? promptTokens,
            int? completionTokens,
            int? totalTokens)
        {
            var nbTranslationsAdded = ParseAndAddTranslations(_transcription, _translation, responseReceived);

            // Update UI for each item
            foreach (var item in Items)
            {
                context.DefaultUpdateHandler(
                    "Translation",
                    this.ToolAction,
                    item.TranslatedTexts.FirstOrDefault(f => f.Id == _translation.Id)?.Text);
            }

            // Add the cost to the translation
            _translation.Costs.Add(
                new TranslationCost(
                    taskName,
                    timeTaken,
                    nbTranslationsAdded,
                    promptTokens,
                    completionTokens,
                    totalTokens));
        }

        public override string NbItemsString() => $"{this.Items.Length} texts";

        public static int ParseAndAddTranslations(Transcription transcription, Translation translation, string responseReceived)
        {
            var nbTranslationsAdded = 0;
            var result = ParseAndFixJson(responseReceived);

            foreach (var item in result)
            {
                var startTime = (string)item.StartTime;
                var original = (string)item.Original;
                var translatedText = (string)item.Translation;

                if (translatedText != null)
                {
                    // Find the matching transcribed text
                    var startTimeSpan = ParseTimeString(startTime);
                    var originalItem = transcription.Items
                        .FirstOrDefault(f => f.StartTime == startTimeSpan && f.Text == original)
                        ?? transcription.Items.FirstOrDefault(f => f.StartTime == startTimeSpan)
                        ?? transcription.Items.FirstOrDefault(f => f.Text == original);

                    if (originalItem != null)
                    {
                        nbTranslationsAdded++;
                        originalItem.TranslatedTexts.Add(
                            new TranslatedText(
                                translation.Id,
                                translatedText));
                    }
                }
            }

            return nbTranslationsAdded;
        }

        private static TimeSpan ParseTimeString(string timeString)
        {
            // TODO FIX

            // Parse format like "1:23.456" or "01:23.456"
            var parts = timeString.Split(':');
            if (parts.Length == 2)
            {
                var minutes = int.Parse(parts[0]);
                var secondsParts = parts[1].Split('.');
                var seconds = int.Parse(secondsParts[0]);
                var milliseconds = secondsParts.Length > 1 ? int.Parse(secondsParts[1]) : 0;
                return new TimeSpan(0, 0, minutes, seconds, milliseconds);
            }
            return TimeSpan.Parse(timeString);
        }
    }
}