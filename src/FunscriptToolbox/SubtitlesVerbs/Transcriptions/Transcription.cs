using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class Transcription : TimedItemWithMetadataCollection<TranscribedItem>
    {
        [JsonProperty(Order = 10)]
        public Language Language { get; }
        [JsonProperty(Order = 11, TypeNameHandling = TypeNameHandling.Auto)]
        public object CurrentJobState { get; set; }
        [JsonProperty(Order = 12)]
        public bool IsFinished { get; private set; }

        public Transcription(
            string id,
            Language language,
            string metadataAlwaysProduced,
            object currentJobState = null,
            bool isFinished = false,
            IEnumerable<TranscribedItem> items = null, 
            IEnumerable<Cost> costs = null)
            : base(id, metadataAlwaysProduced, items, costs)
        {
            Language = language;
            CurrentJobState = currentJobState;
            IsFinished = isFinished;
        }

        public override TranscribedItem AddNewItem(
            TimeSpan startTime, 
            TimeSpan endTime, 
            MetadataCollection extraMetadatas)
        {
            var newItem = new TranscribedItem(startTime, endTime, metadata: extraMetadatas);
            this.Items.Add(newItem);
            return newItem;
        }

        public void MarkAsFinished()
        {
            this.Items.Sort((a, b) => (int)(a.StartTime.TotalMilliseconds - b.StartTime.TotalMilliseconds));
            this.IsFinished = true;
        }
    }
}