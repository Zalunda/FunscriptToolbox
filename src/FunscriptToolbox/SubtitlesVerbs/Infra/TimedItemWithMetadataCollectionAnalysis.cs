using System.Collections.Generic;
using System.Linq;
using System;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using FunscriptToolbox.SubtitlesVerbs.Transcriptions;

namespace FunscriptToolbox.SubtitlesVerbs
{
    public class TimedItemWithMetadataCollectionAnalysis
    {
        public class TimingMatch
        {
            public TimedItemWithMetadata Item { get; }
            public ITiming Timing { get; }
            public List<TranscribedWord> Words { get; } = new List<TranscribedWord>();

            public string WordsText => string.Join(string.Empty, this.Words.Select(f => f.Text));

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
                bool lastWordWasException = false;
                var transcribedItem = item as TranscribedItem;

                foreach (var word in (transcribedItem?.Words.Length > 0)
                    ? transcribedItem.Words
                    : new[] { new TranscribedWord(item.StartTime, item.EndTime, string.Empty, 0) })
                {
                    // Check if this is a special case that should follow previous word
                    bool isSpecialCase = word.Text.EndsWith("...") ||
                                        word.Text.EndsWith(",") || word.Text.EndsWith("\u3001") || /* , */
                                        word.Text.EndsWith(".") || word.Text.EndsWith("\u3002") || /* . */
                                        word.Text.EndsWith("?") ||
                                        word.Text.EndsWith("!");

                    if (isSpecialCase && lastAssignedMatch.ContainsKey(item))
                    {
                        // Add to the same timing as the previous word
                        lastAssignedMatch[item].Words.Add(word);
                        lastWordWasException = true;
                        continue;
                    }

                    // Find all overlapping timings for this word
                    var overlappingTimings = new List<(ITiming Timing, TimingOverlap<TranscribedWord, ITiming> Overlap)>();
                    var wordMidpoint = word.StartTime.Ticks + (word.EndTime.Ticks - word.StartTime.Ticks) / 2;
                    var closestTiming = (Timing: (ITiming) null, Distance: Int64.MaxValue);

                    foreach (var timing in timings)
                    {
                        var overlap = TimingOverlap<TranscribedWord, ITiming>.CalculateOverlap(word, timing);
                        if (overlap != null)
                        {
                            overlappingTimings.Add((timing, overlap));
                        }

                        var timingMidpoint = timing.StartTime.Ticks + (timing.EndTime.Ticks - timing.StartTime.Ticks) / 2;
                        var distance = Math.Abs(timingMidpoint - wordMidpoint);
                        if (distance < closestTiming.Distance)
                        {
                            if (!lastWordWasException || timingMidpoint > wordMidpoint)
                            {
                                closestTiming = (timing, distance);
                            }
                        }
                    }
                    if (closestTiming.Timing == null)
                    {
                        // Fix for when the last subtitle of a file is a "LastWordException"
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
                        match.Words.Add(word);
                    }
                    else if (overlappingTimings.Count == 1)
                    {
                        // Word fits completely in one timing
                        match = matchAccumulator.FirstOrDefault(m =>
                            m.Item == item &&
                            m.Timing == overlappingTimings[0].Timing);
                        if (match == null)
                        {
                            match = new TimingMatch(item, overlappingTimings[0].Timing);
                            matchAccumulator.Add(match);
                        }

                        match.Words.Add(word);
                    }
                    else
                    {
                        // Word overlaps multiple timings - decision logic
                        var bestMatch = lastWordWasException
                            ? overlappingTimings.Last()
                            : overlappingTimings
                            .OrderByDescending(x => x.Overlap.OverlapB) // Order by how much of the timing is covered
                            .ThenByDescending(x => x.Overlap.OverlapA) // Then by how much of the word is covered
                            .First();

                        match = matchAccumulator.FirstOrDefault(m =>
                            m.Item == item &&
                            m.Timing == bestMatch.Timing);
                        if (match == null)
                        {
                            match = new TimingMatch(item, bestMatch.Timing);
                            matchAccumulator.Add(match);
                        }

                        match.Words.Add(word);
                    }

                    // Remember this match for the next word
                    lastAssignedMatch[item] = match;
                    lastWordWasException = false;
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
            NbTimingsWithTranscription = timings.Length - TimingsWithoutItem.Length;
        }

        public TimedItemWithMetadataCollection Container { get; }
        public ITiming[] Timings { get; }
        public ITiming[] TimingsWithoutItem { get; }
        public Dictionary<ITiming, TimingMatch[]> TimingsWithOverlapItems { get; }
        public Dictionary<TimedItemWithMetadata, TimingMatch[]> ItemsWithOverlapTimings { get; }
        public List<TimedItemWithMetadata> ExtraItems { get; }
        public int NbTimingsWithTranscription { get; }
    }
}
