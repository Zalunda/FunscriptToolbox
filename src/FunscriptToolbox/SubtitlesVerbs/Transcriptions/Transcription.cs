using FunscriptToolbox.SubtitlesVerbs.Infra;
using FunscriptToolbox.SubtitlesVerbs.Translations;
using System;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class Transcription : TimedItemWithMetadataCollection<TranscribedItem>
    {
        public string Id { get; private set; }
        public Language Language { get; }
        public bool IsFinished { get; private set; }
        public List<Translation> Translations { get; }
        public override string FullId => $"{this.Id}";

        public Transcription(
            string id,
            Language language,
            bool isFinished = false,
            IEnumerable<TranscribedItem> items = null, 
            IEnumerable<Cost> costs = null,
            IEnumerable<Translation> translations = null)
            : base(items, costs)
        {
            Id = id;
            Language = language;
            IsFinished = isFinished;
            Translations = new List<Translation>(translations ?? Array.Empty<Translation>());
        }

        public override TranscribedItem AddNewItem(TimeSpan startTime, TimeSpan endTime, MetadataCollection extraMetadatas)
        {
            var newItem = new TranscribedItem(startTime, endTime, metadata: extraMetadatas);
            this.Items.Add(newItem);
            return newItem;
        }

        public void Rename(string newId)
        {
            this.Id = newId;
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