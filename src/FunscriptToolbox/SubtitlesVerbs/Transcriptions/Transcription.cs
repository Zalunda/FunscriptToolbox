using FunscriptToolbox.SubtitlesVerbs.Translations;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class Transcription
    {
        public string Id { get; }
        public Language Language { get; }
        public TranscribedText[] Items { get; }
        public TranscriptionCost[] Costs { get; }
        public List<Translation> Translations { get; }

        public Transcription(
            string id,
            Language language,
            IEnumerable<TranscribedText> items, 
            IEnumerable<TranscriptionCost> costs,
            IEnumerable<Translation> translations = null)
        {
            Id = id;
            Language = language;
            Items = items.ToArray();
            Costs = costs.ToArray();
            Translations = new List<Translation>(translations ?? Array.Empty<Translation>());
        }

        public int RemoveTranslation(string translationId)
        {
            var nbTranslatedTexts = 0;
            foreach (var translation in this
                .Translations
                .Where(t => t.Id == translationId)
                .ToArray())
            {
                this.Translations.Remove(translation);
            }

            foreach (var transcribedText in this.Items)
            {
                foreach (var translatedText in transcribedText
                    .TranslatedTexts
                    .Where(tt => tt.Id == translationId)
                    .ToArray())
                {
                    transcribedText.TranslatedTexts.Remove(translatedText);
                    nbTranslatedTexts++;
                }
            }
            return nbTranslatedTexts;
        }
    }
}