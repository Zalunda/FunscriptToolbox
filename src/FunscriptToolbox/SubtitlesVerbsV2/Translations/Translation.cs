using FunscriptToolbox.SubtitlesVerbV2;
using System;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbsV2.Translations
{
    public class Translation
    {
        public string Id { get; }
        public Language Language { get; }
        public List<TranslationCost> Costs { get; }

        public Translation(
            string id,
            Language language,
            IEnumerable<TranslationCost> costs = null)
        {
            Id = id;
            Language = language;
            Costs = new List<TranslationCost>(costs ?? Array.Empty<TranslationCost>());
        }
    }
}