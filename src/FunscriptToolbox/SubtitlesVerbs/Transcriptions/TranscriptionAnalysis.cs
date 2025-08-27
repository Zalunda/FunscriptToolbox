using System.Collections.Generic;
using System.Linq;
using System;
using FunscriptToolbox.SubtitlesVerbs.Infra;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscriptionAnalysis<T> where T : class, ITiming
    {
        public class TranscriptionTimingMatch
        {
            public TranscribedItem TranscribedItems { get; }
            public T Timing { get; }
            public List<TranscribedWord> Words { get; } = new List<TranscribedWord>();

            public string WordsText => string.Join(string.Empty, this.Words.Select(f => f.Text));

            public TranscriptionTimingMatch(TranscribedItem item, T timing)
            {
                TranscribedItems = item;
                Timing = timing;
            }
        }

        public static TranscriptionAnalysis<T> From(
            Transcription transcription,
            T[] timings)
        {
            var matchAccumulator = new List<TranscriptionTimingMatch>();
            var lastAssignedMatch = new Dictionary<TranscribedItem, TranscriptionTimingMatch>();
            return null;

            //foreach (var item in transcription.Items)
            //{
            //    bool lastWordWasException = false;

            //    foreach (var word in (item.Words.Length > 0)
            //        ? item.Words
            //        : new[] { new TranscribedWord(item.StartTime, item.EndTime, item.Metadata.VoiceText, 0) })
            //    {
            //        // Check if this is a special case that should follow previous word
            //        bool isSpecialCase = word.Text.EndsWith("...") ||
            //                            word.Text.EndsWith(",") || word.Text.EndsWith("\u3001") || /* , */
            //                            word.Text.EndsWith(".") || word.Text.EndsWith("\u3002") || /* . */
            //                            word.Text.EndsWith("?") ||
            //                            word.Text.EndsWith("!");

            //        if (isSpecialCase && lastAssignedMatch.ContainsKey(item))
            //        {
            //            // Add to the same timing as the previous word
            //            lastAssignedMatch[item].Words.Add(word);
            //            lastWordWasException = true;
            //            continue;
            //        }
                    
            //        // Find all overlapping timings for this word
            //        var overlappingTimings = new List<(T Timing, TimingOverlap<TranscribedWord, T> Overlap)>();
            //        var wordMidpoint = word.StartTime.Ticks + (word.EndTime.Ticks - word.StartTime.Ticks) / 2;
            //        var closestTiming = (Timing: default(T), Distance: Int64.MaxValue);

            //        foreach (var timing in timings)
            //        {
            //            var overlap = TimingOverlap<TranscribedWord, T>.CalculateOverlap(word, timing);
            //            if (overlap != null)
            //            {
            //                overlappingTimings.Add((timing, overlap));
            //            }

            //            var timingMidpoint = timing.StartTime.Ticks + (timing.EndTime.Ticks - timing.StartTime.Ticks) / 2;
            //            var distance = Math.Abs(timingMidpoint - wordMidpoint);
            //            if (distance < closestTiming.Distance)
            //            {
            //                if (!lastWordWasException || timingMidpoint > wordMidpoint)
            //                {
            //                    closestTiming = (timing, distance);
            //                }
            //            }
            //        }
            //        if (closestTiming.Timing == null)
            //        {
            //            // Fix for when the last subtitle of a file is a "LastWordException"
            //            closestTiming = (timings.Last(), 0);
            //        }

            //        TranscriptionTimingMatch match;
            //        if (overlappingTimings.Count == 0)
            //        {
            //            // No overlap - use closest timing
            //            match = matchAccumulator.FirstOrDefault(m =>
            //                m.TranscribedItems == item &&
            //                m.Timing == closestTiming.Timing);
            //            if (match == null)
            //            {
            //                match = new TranscriptionTimingMatch(item, closestTiming.Timing);
            //                matchAccumulator.Add(match);
            //            }
            //            match.Words.Add(word);
            //        }
            //        else if (overlappingTimings.Count == 1)
            //        {
            //            // Word fits completely in one timing
            //            match = matchAccumulator.FirstOrDefault(m =>
            //                m.TranscribedItems == item &&
            //                m.Timing == overlappingTimings[0].Timing);
            //            if (match == null)
            //            {
            //                match = new TranscriptionTimingMatch(item, overlappingTimings[0].Timing);
            //                matchAccumulator.Add(match);
            //            }

            //            match.Words.Add(word);
            //        }
            //        else
            //        {
            //            // Word overlaps multiple timings - decision logic
            //            var bestMatch = lastWordWasException
            //                ? overlappingTimings.Last()
            //                : overlappingTimings
            //                .OrderByDescending(x => x.Overlap.OverlapB) // Order by how much of the timing is covered
            //                .ThenByDescending(x => x.Overlap.OverlapA) // Then by how much of the word is covered
            //                .First();

            //            match = matchAccumulator.FirstOrDefault(m =>
            //                m.TranscribedItems == item &&
            //                m.Timing == bestMatch.Timing);
            //            if (match == null)
            //            {
            //                match = new TranscriptionTimingMatch(item, bestMatch.Timing);
            //                matchAccumulator.Add(match);
            //            }

            //            match.Words.Add(word);
            //        }

            //        // Remember this match for the next word
            //        lastAssignedMatch[item] = match;
            //        lastWordWasException = false;
            //    }
            //}

            //// Convert to the expected format           
            //return new TranscriptionAnalysis<T>(
            //    transcription,
            //    timings,
            //    matchAccumulator
            //        .GroupBy(m => m.Timing)
            //        .ToDictionary(
            //            g => g.Key,
            //            g => g.ToArray()
            //        ),
            //    matchAccumulator
            //        .GroupBy(m => m.TranscribedItems)
            //        .ToDictionary(
            //            g => g.Key,
            //            g => g.ToArray()
            //        ));
        }

        public TranscriptionAnalysis(
            Transcription transcription,
            T[] timings,
            Dictionary<T, TranscriptionTimingMatch[]> timingsWithOverlapTranscribedItems,
            Dictionary<TranscribedItem, TranscriptionTimingMatch[]> transcribedItemWithOverlapTimings)
        {
            Transcription = transcription;
            Timings = timings;
            TimingsWithoutTranscription = timings
                .Where(timing => !timingsWithOverlapTranscribedItems.ContainsKey(timing))
                .ToArray();
            TimingsWithOverlapTranscribedItems = timingsWithOverlapTranscribedItems;
            TranscribedItemWithOverlapTimings = transcribedItemWithOverlapTimings;
            //ExtraTranscriptions = transcription.Items
            //    .Where(item => !transcribedItemWithOverlapTimings.ContainsKey(item))
            //    .ToArray();
            //NbTimingsWithTranscription = timings.Length - TimingsWithoutTranscription.Length;
        }

        public Transcription Transcription { get; }
        public T[] Timings { get; }
        public T[] TimingsWithoutTranscription { get; }
        public Dictionary<T, TranscriptionTimingMatch[]> TimingsWithOverlapTranscribedItems { get; }
        public Dictionary<TranscribedItem, TranscriptionTimingMatch[]> TranscribedItemWithOverlapTimings { get; }
        public TranscribedItem[] ExtraTranscriptions { get; }
        public int NbTimingsWithTranscription { get; }
    }
}
