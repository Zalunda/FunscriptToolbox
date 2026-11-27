using System.Collections.Generic;
using System.Linq;
using System;
using FunscriptToolbox.SubtitlesVerbs.Infra;

namespace FunscriptToolbox.SubtitlesVerbs
{
    public class TimedItemWithMetadataCollectionAnalysis
    {
        /// <summary>A part of the text of an item, with its share of the item's duration (see SplitEvenly).</summary>
        public class TextBlock : ITiming
        {
            public TimeSpan StartTime { get; }
            public TimeSpan EndTime { get; }
            public TimeSpan Duration => EndTime - StartTime;
            public string Text { get; }

            public TextBlock(TimeSpan startTime, TimeSpan endTime, string text)
            {
                StartTime = startTime;
                EndTime = endTime;
                Text = text;
            }
        }

        public class TimingMatch
        {
            public TimedItemWithMetadata Item { get; }
            public ITiming Timing { get; }
            public List<TextBlock> Blocks { get; } = new List<TextBlock>();

            public TimingMatch(TimedItemWithMetadata item, ITiming timing)
            {
                this.Item = item;
                this.Timing = timing;
            }
        }

        public static TimedItemWithMetadataCollectionAnalysis From(
            TimedItemWithMetadataCollection container,
            IEnumerable<ITiming> timings)
        {
            var matchAccumulator = new List<TimingMatch>();
            var lastAssignedMatch = new Dictionary<TimedItemWithMetadata, TimingMatch>();

            foreach (var item in container.GetItems())
            {
                bool lastBlockWasException = false;

                // (the text of the item spread evenly over its duration: the items don't keep the times of their words)
                foreach (var block in SplitEvenly(item, (container.MetadataAlwaysProduced == null ? null : item.Metadata.Get(container.MetadataAlwaysProduced)) ?? new string('.', 15)))
                {
                    // Check if this is a special case that should follow previous block
                    bool isSpecialCase = block.Text.EndsWith("...") ||
                                        block.Text.EndsWith(",") || block.Text.EndsWith("\u3001") || /* , */
                                        block.Text.EndsWith(".") || block.Text.EndsWith("\u3002") || /* . */
                                        block.Text.EndsWith("?") ||
                                        block.Text.EndsWith("!");

                    if (isSpecialCase && lastAssignedMatch.ContainsKey(item))
                    {
                        // Add to the same timing as the previous block
                        lastAssignedMatch[item].Blocks.Add(block);
                        lastBlockWasException = true;
                        continue;
                    }

                    // Find all overlapping timings for this block
                    var overlappingTimings = new List<(ITiming Timing, TimingOverlap<TextBlock, ITiming> Overlap)>();
                    var blockMidpoint = block.StartTime.Ticks + (block.EndTime.Ticks - block.StartTime.Ticks) / 2;
                    var closestTiming = (Timing: (ITiming) null, Distance: Int64.MaxValue);

                    foreach (var timing in timings)
                    {
                        var overlap = TimingOverlap<TextBlock, ITiming>.CalculateOverlap(block, timing);
                        if (overlap != null)
                        {
                            overlappingTimings.Add((timing, overlap));
                        }

                        var timingMidpoint = timing.StartTime.Ticks + (timing.EndTime.Ticks - timing.StartTime.Ticks) / 2;
                        var distance = Math.Abs(timingMidpoint - blockMidpoint);
                        if (distance < closestTiming.Distance)
                        {
                            if (!lastBlockWasException || timingMidpoint > blockMidpoint)
                            {
                                closestTiming = (timing, distance);
                            }
                        }
                    }
                    if (closestTiming.Timing == null)
                    {
                        // Fix for when the last subtitle of a file is a "LastBlockException"
                        closestTiming = (timings.Last(), 0);
                    }

                    TimingMatch match;
                    if (overlappingTimings.Count == 0)
                    {
                        // No overlap - use closest timing
                        match = matchAccumulator.FirstOrDefault(m =>
                            m.Item == item &&
                            m.Timing == closestTiming.Timing);
                        if (match == null)
                        {
                            match = new TimingMatch(item, closestTiming.Timing);
                            matchAccumulator.Add(match);
                        }
                        match.Blocks.Add(block);
                    }
                    else if (overlappingTimings.Count == 1)
                    {
                        // Block fits completely in one timing
                        match = matchAccumulator.FirstOrDefault(m =>
                            m.Item == item &&
                            m.Timing == overlappingTimings[0].Timing);
                        if (match == null)
                        {
                            match = new TimingMatch(item, overlappingTimings[0].Timing);
                            matchAccumulator.Add(match);
                        }

                        match.Blocks.Add(block);
                    }
                    else
                    {
                        // Block overlaps multiple timings - decision logic
                        var bestMatch = lastBlockWasException
                            ? overlappingTimings.Last()
                            : overlappingTimings
                            .OrderByDescending(x => x.Overlap.OverlapB) // Order by how much of the timing is covered
                            .ThenByDescending(x => x.Overlap.OverlapA) // Then by how much of the block is covered
                            .First();

                        match = matchAccumulator.FirstOrDefault(m =>
                            m.Item == item &&
                            m.Timing == bestMatch.Timing);
                        if (match == null)
                        {
                            match = new TimingMatch(item, bestMatch.Timing);
                            matchAccumulator.Add(match);
                        }

                        match.Blocks.Add(block);
                    }

                    // Remember this match for the next block
                    lastAssignedMatch[item] = match;
                    lastBlockWasException = false;
                }
            }

            // Convert to the expected format           
            return new TimedItemWithMetadataCollectionAnalysis(
                container,
                timings.ToArray(),
                matchAccumulator
                    .GroupBy(m => m.Timing)
                    .ToDictionary(
                        g => g.Key,
                        g => g.ToArray()),
                matchAccumulator
                    .GroupBy(m => m.Item)
                    .ToDictionary(
                        g => g.Key,
                        g => g.ToArray()));
        }

        private static IEnumerable<TextBlock> SplitEvenly(ITiming timing, string text)
        {
            text = text.Length == 0
                ? " "
                : text;

            // Determine the number of blocks. It's the smaller of the text length or the max of 15.
            int nbBlocks = Math.Min(text.Length, 15);

            // Calculate the time duration for each block. This part remains the same.
            var timePerBlock = new TimeSpan(timing.Duration.Ticks / nbBlocks);
            var currentStartTime = timing.StartTime;

            // Calculate the base number of characters in each block.
            int baseSize = text.Length / nbBlocks;

            // Calculate how many blocks will get an extra character.
            int remainder = text.Length % nbBlocks;

            // Keep track of our current position in the string.
            int currentIndex = 0;

            for (int i = 0; i < nbBlocks; i++)
            {
                // The first 'remainder' blocks get one extra character to distribute them evenly.
                int blockSize = baseSize + (i < remainder ? 1 : 0);

                // Get the substring for the current block.
                string blockText = text.Substring(currentIndex, blockSize);

                // Create the new TextBlock.
                yield return new TextBlock(currentStartTime, currentStartTime + timePerBlock, blockText);

                // Move the start time and index forward for the next iteration.
                currentStartTime += timePerBlock;
                currentIndex += blockSize;
            }
        }

        public TimedItemWithMetadataCollectionAnalysis(
            TimedItemWithMetadataCollection container,
            ITiming[] timings,
            Dictionary<ITiming, TimingMatch[]> timingsWithOverlapItems,
            Dictionary<TimedItemWithMetadata, TimingMatch[]> itemWithOverlapTimings)
        {
            Container = container;
            Timings = timings;
            TimingsWithoutItem = timings
                .Where(timing => !timingsWithOverlapItems.ContainsKey(timing))
                .ToArray();
            TimingsWithOverlapItems = timingsWithOverlapItems;
            ItemsWithOverlapTimings = itemWithOverlapTimings;
            ExtraItems = container.GetItems()
                .Where(item => !itemWithOverlapTimings.ContainsKey(item))
                .OrderBy(item => item.StartTime)
                .ToList();
            NbTimingsWithItem = timings.Length - TimingsWithoutItem.Length;
        }

        public TimedItemWithMetadataCollection Container { get; }
        public ITiming[] Timings { get; }
        public ITiming[] TimingsWithoutItem { get; }
        public Dictionary<ITiming, TimingMatch[]> TimingsWithOverlapItems { get; }
        public Dictionary<TimedItemWithMetadata, TimingMatch[]> ItemsWithOverlapTimings { get; }
        public List<TimedItemWithMetadata> ExtraItems { get; }
        public int NbTimingsWithItem { get; }
    }
}
