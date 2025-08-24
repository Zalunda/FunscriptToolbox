using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Translations
{
    public class Translation : ITimedObjectWithMetadataCollection
    {
        public string Id { get; }
        public Language Language { get; }
        public bool IsFinished { get; private set; }

        public List<TranslatedText> Items { get; }
        public List<TranslationCost> Costs { get; }

        ICollection<TimedObjectWithMetadata> ITimedObjectWithMetadataCollection.Items => this.Items.Cast<TimedObjectWithMetadata>().ToArray();

        public Translation(
            string id,
            Language language,
            bool isFinished = false,
            IEnumerable<TranslatedText> items = null,
            IEnumerable<TranslationCost> costs = null)
        {
            Id = id;
            Language = language;
            IsFinished = isFinished;
            Items = new List<TranslatedText>(items ?? Array.Empty<TranslatedText>());
            Costs = new List<TranslationCost>(costs ?? Array.Empty<TranslationCost>());
        }

        internal void MarkAsFinished()
        {
            this.Items.Sort((a, b) => (int)(a.StartTime.TotalMilliseconds - b.StartTime.TotalMilliseconds));
            this.IsFinished = true;
        }
    }
}