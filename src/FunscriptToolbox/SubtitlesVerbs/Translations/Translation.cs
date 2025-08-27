using FunscriptToolbox.SubtitlesVerbs.Infra;
using FunscriptToolbox.SubtitlesVerbs.Transcriptions;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Translations
{
    public class Translation : TimedItemWithMetadataCollection<TranslatedItem>
    {
        public Transcription Parent { get; private set; }
        public string Id { get; }
        public Language Language { get; }
        public bool IsFinished { get; private set; }
        public override string FullId => $"{this.Parent.Id}_{this.Id}";

        public Translation(
            Transcription parent,
            string id,
            Language language,
            bool isFinished = false,
            IEnumerable<TranslatedItem> items = null,
            IEnumerable<Cost> costs = null)
            : base(items, costs)
        {
            Parent = parent;
            Id = id;
            Language = language;
            IsFinished = isFinished;
        }

        public void EnsureParent(Transcription parent)
        {
            this.Parent = parent;
        }

        public override TranslatedItem AddNewItem(TimeSpan startTime, TimeSpan endTime, MetadataCollection extraMetadatas)
        {
            var transcribedItem = this.Parent.Items
                .FirstOrDefault(transcribedItem => transcribedItem.StartTime == startTime);

            var newItem = new TranslatedItem(transcribedItem, startTime, endTime, extraMetadatas);
            this.Items.Add(newItem);
            return newItem;
        }

        internal void MarkAsFinished()
        {
            this.Items.Sort((a, b) => (int)(a.StartTime.TotalMilliseconds - b.StartTime.TotalMilliseconds));
            this.IsFinished = true;
        }
    }
}