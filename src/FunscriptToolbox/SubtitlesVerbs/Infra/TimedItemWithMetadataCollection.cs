using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public abstract class TimedItemWithMetadataCollection
    {
        public abstract string FullId { get; }

        public abstract IEnumerable<TimedItemWithMetadata> GetItems();
    }

    public abstract class TimedItemWithMetadataCollection<TItem> : TimedItemWithMetadataCollection where TItem : TimedItemWithMetadata
    {
        public List<TItem> Items { get; }
        public List<Cost> Costs { get; }

        public override IEnumerable<TimedItemWithMetadata> GetItems() => this.Items.Cast<TimedItemWithMetadata>();

        public ITiming[] GetTimings() => this.Items.Cast<ITiming>().ToArray();

        public TimedItemWithMetadataCollection(
            IEnumerable<TItem> items = null,
            IEnumerable<Cost> costs = null)
        {
            this.Items = new List<TItem>(items ?? Array.Empty<TItem>());
            this.Costs = new List<Cost>(costs ?? Array.Empty<Cost>());
        }

        public abstract TItem AddNewItem(TimeSpan startTime, TimeSpan endTime, MetadataCollection extraMetadatas);
    }
}