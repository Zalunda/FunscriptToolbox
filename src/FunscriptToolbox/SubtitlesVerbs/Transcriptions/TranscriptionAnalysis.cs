using System.Collections.Generic;
using System.Linq;
using System;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscriptionAnalysis
    {
        public static TranscriptionAnalysis From(
            Transcription transcription,
            SubtitleForcedTiming[] forcedTimings,
            int minimumPercentage)
        {
            var extraTranscriptions = new List<TranscribedText>();
            var matchedTranscribedTexts = new List<TimingOverlap>();
            foreach (var tt in transcription.Items)
            {
                var matches = forcedTimings
                    .Select(forcedTiming => TimingOverlap.From(transcription.Id, tt, forcedTiming))
                    .Where(f => f != null)
                    .ToArray();
                if (matches.Length == 0)
                {
                    extraTranscriptions.Add(tt);
                }
                else if (matches.Length == 1)
                {
                    matchedTranscribedTexts.Add(matches.First());
                }
                else 
                {
                    var totalTimeMatched = matches.Select(m => m.Duration.TotalMilliseconds).Sum();

                    var index = 1;
                    foreach (var match in matches)
                    {
                        match.OverlapInfo = new MultiTimingsOverlap()
                        {
                            Index = index++,
                            NumberOverlap = matches.Length,
                            PercentageTime = (int)(100 * match.Duration.TotalMilliseconds / totalTimeMatched)
                        };
                        if (match.OverlapInfo.PercentageTime > minimumPercentage)
                        {
                            matchedTranscribedTexts.Add(match);
                        }
                    }
                }
            }

            return new TranscriptionAnalysis(
                transcription,
                forcedTimings
                .ToDictionary(
                    forcedTiming => forcedTiming,
                    forcedTiming => matchedTranscribedTexts
                            .Where(match => match.ForcedTiming == forcedTiming)
                            .ToArray()),
                extraTranscriptions.ToArray());
        }

        public TranscriptionAnalysis(
            Transcription transcription,
            Dictionary<SubtitleForcedTiming, TimingOverlap[]> forcedTimingsWithOverlapTranscribedTexts,
            TranscribedText[] unmatchedTranscribedTexts)
        {
            Transcription = transcription;
            ForcedTimingsWithOverlapTranscribedTexts = forcedTimingsWithOverlapTranscribedTexts;
            ExtraTranscriptions = unmatchedTranscribedTexts.ToArray();
            NbWithTranscription = forcedTimingsWithOverlapTranscribedTexts.Count(f => f.Value.Length > 0);
            NbWithoutTranscription = forcedTimingsWithOverlapTranscribedTexts.Count - NbWithTranscription;
        }

        public Transcription Transcription { get; }
        public Dictionary<SubtitleForcedTiming, TimingOverlap[]> ForcedTimingsWithOverlapTranscribedTexts { get; }
        public TranscribedText[] ExtraTranscriptions { get; }
        public int NbWithTranscription { get; }
        public int NbWithoutTranscription { get; }

        public class TimingOverlap
        {
            public static TimingOverlap From(
                string transcriptionId,
                TranscribedText tt)
            {
                return new TimingOverlap(
                        transcriptionId,
                        tt.StartTime,
                        tt.EndTime,
                        tt,
                        null);
            }

            public static TimingOverlap From(
                string transcriptionId,
                TranscribedText tt,
                SubtitleForcedTiming forcedTiming)
            {
                var overlapStartTime = forcedTiming.StartTime > tt.StartTime ? forcedTiming.StartTime : tt.StartTime;
                var overlapEndTime = forcedTiming.EndTime < tt.EndTime ? forcedTiming.EndTime : tt.EndTime;
                return (overlapStartTime < overlapEndTime)
                    ? new TimingOverlap(
                        transcriptionId,
                        overlapStartTime,
                        overlapEndTime,
                        tt,
                        forcedTiming)
                    : null;
            }

            private TimingOverlap(
                string transcriptionId,
                TimeSpan overlapStartTime,
                TimeSpan overlapEndTime,
                TranscribedText tt,
                SubtitleForcedTiming forcedTiming)
            {
                TranscriptionId = transcriptionId;
                StartTime = overlapStartTime;
                EndTime = overlapEndTime;
                ForcedTiming = forcedTiming;
                TranscribedText = tt;
                if (forcedTiming != null)
                {
                    MatchPercentage = (int)(100 * (Math.Min(overlapEndTime.TotalMilliseconds, forcedTiming.EndTime.TotalMilliseconds)
                        - Math.Max(overlapStartTime.TotalMilliseconds, forcedTiming.StartTime.TotalMilliseconds)) / forcedTiming.Duration.TotalMilliseconds);
                }
            }

            public string TranscriptionId { get; }
            public TimeSpan StartTime { get; }
            public TimeSpan EndTime { get; }
            public TimeSpan Duration => EndTime - StartTime;
            public TranscribedText TranscribedText { get; }
            public SubtitleForcedTiming ForcedTiming { get; }
            public int MatchPercentage { get; }

            public MultiTimingsOverlap OverlapInfo { get; set; }
        }

        public class MultiTimingsOverlap
        {
            public int Index { get; set; }
            public int NumberOverlap { get; set; }
            public int PercentageTime { get; set; }

            public override string ToString()
            {
                return $"[{Index}/{NumberOverlap}, {PercentageTime}%]";
            }
        }
    }
}
