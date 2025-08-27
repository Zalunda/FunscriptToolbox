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
        [JsonProperty(Order = 11)]
        public bool IsFinished { get; private set; }

        public Transcription(
            string id,
            Language language,
            bool isFinished = false,
            IEnumerable<TranscribedItem> items = null, 
            IEnumerable<Cost> costs = null)
            : base(id, items, costs)
        {
            Language = language;
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

        public TranscriptionAnalysis<T> GetAnalysis<T>(
            T[] timings) where T : class, ITiming
        {
            return (timings == null)
                    ? null :
                    TranscriptionAnalysis<T>.From(
                        this,
                        timings);
        }
    }
}