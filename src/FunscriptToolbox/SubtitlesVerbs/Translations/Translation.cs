using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Translations
{
    public class Translation
    {
        public string Id { get; }
        public Language Language { get; }
        public TranslationExample[] TranslationExamples { get; private set; }

        public List<TranslationCost> Costs { get; }

        public Translation(
            string id,
            Language language,
            IEnumerable<TranslationExample> translationExamples = null,
            IEnumerable<TranslationCost> costs = null)
        {
            Id = id;
            Language = language;
            TranslationExamples = (translationExamples ?? Array.Empty<TranslationExample>()).ToArray();
            Costs = new List<TranslationCost>(costs ?? Array.Empty<TranslationCost>());
        }

        internal IEnumerable<TranslationExample> GetTranslationExamples(Language originalLanguage, int nbToReturn)
        {
            if (this.TranslationExamples == null || this.TranslationExamples.Length == 0)
            {
                this.TranslationExamples = TranslationExample.CreateTranslationExamples(originalLanguage, this.Language);
            }

            int startIndex = Math.Max(0, this.TranslationExamples.Length - nbToReturn);
            for (int i = startIndex; i < this.TranslationExamples.Length; i++)
            {
                yield return this.TranslationExamples[i];
            }
        }
    }
}