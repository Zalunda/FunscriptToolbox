using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Translations
{
    public class Translation : TimedItemWithMetadataCollection<TranslatedItem>
    {
        [JsonProperty(Order = 6)]
        public Language Language { get; }

        public Translation(
            string id,
            string metadataAlwaysProduced,
            string[] privateMetadataNames,
            Language language,
            bool isFinished = false,
            IEnumerable<TranslatedItem> items = null,
            IEnumerable<Cost> costs = null)
            : base(id, metadataAlwaysProduced, privateMetadataNames, isFinished, items, costs)
        {
            Language = language;
            IsFinished = isFinished;
        }

        public override TranslatedItem AddNewItem(
            TimeSpan startTime, 
            TimeSpan endTime, 
            MetadataCollection extraMetadatas)
        {
            var newItem = new TranslatedItem(startTime, endTime, extraMetadatas);
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