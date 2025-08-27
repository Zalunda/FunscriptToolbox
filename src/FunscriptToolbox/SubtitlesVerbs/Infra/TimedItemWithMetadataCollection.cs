using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public abstract class TimedItemWithMetadataCollection
    {
        [JsonProperty(Order = 1)]
        public string Id { get; }

        public abstract IEnumerable<TimedItemWithMetadata> GetItems();

        protected TimedItemWithMetadataCollection(string id)
        {
            this.Id = id;
        }
    }

    public abstract class TimedItemWithMetadataCollection<TItem> : TimedItemWithMetadataCollection where TItem : TimedItemWithMetadata
    {
        [JsonProperty(Order = 100)]
        public List<TItem> Items { get; }
        [JsonProperty(Order = 101)]
        public List<Cost> Costs { get; }

        public override IEnumerable<TimedItemWithMetadata> GetItems() => this.Items.Cast<TimedItemWithMetadata>();

        public ITiming[] GetTimings() => this.Items.Cast<ITiming>().ToArray();

        public TimedItemWithMetadataCollection(
            string id,
            IEnumerable<TItem> items = null,
            IEnumerable<Cost> costs = null)
            : base(id)
        {
            this.Items = new List<TItem>(items ?? Array.Empty<TItem>());
            this.Costs = new List<Cost>(costs ?? Array.Empty<Cost>());
        }

        public abstract TItem AddNewItem(TimeSpan startTime, TimeSpan endTime, MetadataCollection extraMetadatas);
    }
}