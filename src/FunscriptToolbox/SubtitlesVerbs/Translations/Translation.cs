using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Translations
{
    public class Translation : TimedItemWithMetadataCollection<TranslatedItem>
    {
        [JsonProperty(Order = 5)]
        public string TranscriptionId { get; }
        [JsonProperty(Order = 6)]
        public string TranslationId { get; }
        [JsonProperty(Order = 7)]
        public Language Language { get; }
        [JsonProperty(Order = 8)]
        public bool IsFinished { get; private set; }

        public Translation(
            string transcriptionId,
            string translationId,
            Language language,
            string metadataAlwaysProduced,
            bool isFinished = false,
            IEnumerable<TranslatedItem> items = null,
            IEnumerable<Cost> costs = null)
            : base($"{transcriptionId}_{translationId}", metadataAlwaysProduced, items, costs)
        {
            TranscriptionId = transcriptionId;
            TranslationId = translationId;
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