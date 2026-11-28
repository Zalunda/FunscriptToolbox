using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.Inputs;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Processors.Refiner
{
    /// <summary>
    /// Snaps subtitle boundaries onto the terrain of the audio (where the speech is), handles overlaps,
    /// splices title cards, and computes summary statistics.
    /// </summary>
    public class TimingRefiner
    {
        // ---------------------------------------------------------------------
        // Constants
        // ---------------------------------------------------------------------

        /// <summary>The shortest clip / subtitle (a boundary never goes closer than this to the other one).</summary>
        internal static readonly TimeSpan MinClipDuration = TimeSpan.FromSeconds(0.05);
        private static readonly TimeSpan MaxSnapDrift = TimeSpan.FromSeconds(1.2);
        internal static readonly TimeSpan MaxFinalClampOverlap = TimeSpan.FromMilliseconds(150);

        // ---------------------------------------------------------------------
        // Settings & state
        // ---------------------------------------------------------------------

        public AudioTerrainMapping TerrainMapping { get; }
        public TimeSpan AudioEndTime { get; }

        /// <summary>Always kept sorted by OriginalStartTime.</summary>
        public List<TimingCandidate> Candidates { get; private set; } = new List<TimingCandidate>();

        private readonly TimingValidatorSettings r_validatorSettings;

        /// <param name="validatorSettings">What is flagged while working (null = nothing is flagged).</param>
        public TimingRefiner(
            PcmAudio fullPcmAudioForRefiner,
            TimingRefinerSettings settings,
            TimingValidatorSettings validatorSettings)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            r_validatorSettings = validatorSettings;
            AudioEndTime = fullPcmAudioForRefiner?.EndTime ?? TimeSpan.MaxValue;

            // Without terrain snapping there is no terrain: the refiner is then only the list of candidates
            if (!settings.Enabled)
            {
                TerrainMapping = null;
            }
            else
            {
                TerrainMapping = new AudioTerrainMapping(fullPcmAudioForRefiner)
                {
                    ValleyReach = settings.ValleyReach,
                };
                TerrainMapping.Compute();
            }
        }

        // ---------------------------------------------------------------------
        // Small helpers
        // ---------------------------------------------------------------------

        /// <summary>Flags a candidate for review, unless the "Validations" setting says to ignore that kind of finding.</summary>
        /// <param name="detail">A sub-kind of the code (ex. the name of the MaybePointless pattern), or null.</param>
        public void Flag(TimingCandidate cand, ValidationCode code, string message, string detail = null)
        {
            if (ModeOf(code) == ValidationMode.Ignore)
                return;

            cand.AddValidationNote(new ValidationNote(code, message, detail));
        }

        /// <summary>What to do for a code (see <see cref="TimingValidatorSettings.ModeOf"/>): Ignore when there are no validator settings.</summary>
        public ValidationMode ModeOf(ValidationCode code)
            => r_validatorSettings?.ModeOf(code) ?? ValidationMode.Ignore;

        private static TimeSpan Shift(TimeSpan t, BoundarySide side, MoveDirection dir, TimeSpan amount)
            => AudioTerrainMapping.MovesLater(side, dir) ? t + amount : t - amount;

        // ---------------------------------------------------------------------
        // Candidate creation (terrain snapping)
        // ---------------------------------------------------------------------

        /// <summary>
        /// Adds a candidate as it is (no snapping, no terrain): both boundaries are the ones of the item, and settled.
        /// The candidates must be added in order of start time.
        /// </summary>
        public void AddCandidateUnrefined(TimedItemWithMetadata originalItem, string originalText)
        {
            var candidate = new TimingCandidate
            {
                OriginalStartTime = originalItem.StartTime,
                OriginalEndTime = originalItem.EndTime,
                OriginalItem = originalItem,
                OriginalText = originalText,
            };
            candidate.Start.Init = candidate.Start.Current = originalItem.StartTime;
            candidate.End.Init = candidate.End.Current = originalItem.EndTime;
            candidate.LockAll();
            candidate.AddTraceRow("Orig", ' ', originalItem.StartTime, originalItem.EndTime, ' ', "", candidate.OriginalText);

            Candidates.Add(candidate);
        }

        /// <summary>The candidates must be added in order of start time.</summary>
        public void AddCandidate(
            TimedItemWithMetadata originalItem,
            string originalText,
            TimingOrigin origin = TimingOrigin.Original)
        {
            var candidate = new TimingCandidate
            {
                OriginalStartTime = originalItem.StartTime,
                OriginalEndTime = originalItem.EndTime,
                OriginalItem = originalItem,
                OriginalText = originalText,
                Origin = origin,
            };

            TimeSpan start = SnapInitialBoundary(candidate, BoundarySide.Start);
            TimeSpan end = SnapInitialBoundary(candidate, BoundarySide.End);

            if (end <= start)
            {
                start = originalItem.StartTime;
                end = originalItem.EndTime;
            }

            EnforceMinimumDuration(candidate, ref start, ref end);

            candidate.Start.Init = start;
            candidate.Start.Current = start;
            candidate.End.Init = end;
            candidate.End.Current = end;
            candidate.LockAll();

            candidate.AddTraceRow("Orig", ' ', originalItem.StartTime, originalItem.EndTime, ' ', "", candidate.OriginalText);
            candidate.Trace("Snap", originalItem.StartTime, originalItem.EndTime, "Snapped on the audio terrain");

            Candidates.Add(candidate);

            // Keep sorted by construction (normally a no-op since the caller adds them in order)
            if (Candidates.Count > 1 && candidate.OriginalStartTime < Candidates[Candidates.Count - 2].OriginalStartTime)
                Candidates = Candidates.OrderBy(c => c.OriginalStartTime).ToList();
        }

        /// <summary>Finds a dip and accepts it only if it is within MaxSnapDrift of 't'.</summary>
        private TimeSpan? FindDipWithinDrift(TimeSpan t, BoundarySide side, MoveDirection dir, DipSearch search)
        {
            var dip = TerrainMapping.FindDip(t, side, dir, search);
            return dip.HasValue && (dip.Value - t).Duration() <= MaxSnapDrift ? dip : null;
        }

        public TimeSpan SnapInitialBoundary(TimingCandidate cand, BoundarySide side)
        {
            var t = side == BoundarySide.Start ? cand.OriginalStartTime : cand.OriginalEndTime;

            // 1. Silent: snap inward towards the speech edge (or the nearest dip)
            if (TerrainMapping.IsSilent(t))
            {
                var inwardLimit = Shift(t, side, MoveDirection.Inward, MaxSnapDrift);

                return FindDipWithinDrift(t, side, MoveDirection.Inward, new DipSearch
                       {
                           MinJump = TimeSpan.MaxValue,
                           StopAtSpeechEdge = true,
                           Inclusive = true,
                           Limit = inwardLimit
                       })
                    ?? FindDipWithinDrift(t, side, MoveDirection.Inward, new DipSearch
                       {
                           Inclusive = true,
                           Limit = inwardLimit
                       })
                    ?? t;
            }

            // 2. Inside a mountain: snap outward to the speech edge (or the nearest dip)
            return FindDipWithinDrift(t, side, MoveDirection.Outward, new DipSearch
                   {
                       StopAtSpeechEdge = true,
                       Inclusive = true,
                       Limit = Shift(t, side, MoveDirection.Outward, MaxSnapDrift)
                   })
                ?? FindDipWithinDrift(t, side, MoveDirection.Outward, new DipSearch { Inclusive = true })
                ?? t;
        }

        /// <summary>Only keeps the starts ordered and the end after the start. There is NO minimum subtitle duration.</summary>
        public void EnforceMinimumDuration(TimingCandidate cand, ref TimeSpan start, ref TimeSpan end)
        {
            var prev = Candidates.LastOrDefault();
            if (prev != null && start <= prev.Start.Current)
            {
                var nextDip = TerrainMapping.FindDip(
                    prev.Start.Current,
                    BoundarySide.Start,
                    MoveDirection.Inward,
                    new DipSearch
                    {
                        Inclusive = false,
                        Limit = cand.OriginalStartTime + MaxSnapDrift
                    });

                if (nextDip.HasValue && (nextDip.Value - cand.OriginalStartTime).Duration() <= MaxSnapDrift)
                {
                    start = nextDip.Value;
                }
                else if (cand.OriginalStartTime > prev.Start.Current)
                {
                    start = cand.OriginalStartTime;
                }
            }

            if (end <= start)
            {
                end = cand.OriginalEndTime > start ? cand.OriginalEndTime : start + MinClipDuration;
            }
        }

        // ---------------------------------------------------------------------
        // Finalization & stats
        // ---------------------------------------------------------------------

        /// <summary>
        /// Adds the title cards (times of the whole timeline) between the other candidates. They are never snapped or checked:
        /// they have no text, their on-screen text is read later (see {GrabOnScreenText} in the output).
        /// </summary>
        public void SpliceTitleCards(IEnumerable<(TimeSpan Start, TimeSpan End)> cards)
        {
            foreach (var (start, end) in cards)
            {
                var cand = new TimingCandidate
                {
                    OriginalStartTime = start,
                    OriginalEndTime = end,
                    OriginalItem = new TimedItemWithMetadata(start, end),
                    OriginalText = "",
                    Origin = TimingOrigin.TitleCard,
                };
                cand.LockAll();
                cand.Start.Init = cand.Start.Current = start;
                cand.End.Init = cand.End.Current = end;

                cand.AddTraceRow("Card", ' ', start, end, ' ', "Title card detected in the video");
                Flag(cand, ValidationCode.TitleCard, "Title card detected (on-screen text to read)");
                Candidates.Add(cand);
            }

            Candidates = Candidates.OrderBy(c => c.OriginalStartTime).ToList();
        }

        /// <summary>Last safety net: clamps slight overlaps at the midpoint, flags the others for review.</summary>
        public void FinalizeOverlaps()
        {
            for (int i = 0; i < Candidates.Count - 1; i++)
            {
                var cur = Candidates[i];
                var next = Candidates[i + 1];

                if (cur.End.Current <= next.Start.Current)
                    continue;

                var overlap = cur.End.Current - next.Start.Current;
                if (overlap <= MaxFinalClampOverlap)
                {
                    var mid = TimeSpan.FromMilliseconds((cur.End.Current.TotalMilliseconds + next.Start.Current.TotalMilliseconds) / 2.0);
                    if (mid - cur.Start.Current >= MinClipDuration && next.End.Current - mid >= MinClipDuration)
                    {
                        var curPrevEnd = cur.End.Current;
                        var nextPrevStart = next.Start.Current;
                        cur.End.Current = mid;
                        next.Start.Current = mid;
                        cur.Trace("Clmp", cur.Start.Current, curPrevEnd, "Clamped minor overlap with next");
                        next.Trace("Clmp", nextPrevStart, next.End.Current, "Clamped minor overlap with prev");
                        continue;
                    }
                }

                Flag(cur, ValidationCode.Overlap, "Overlaps with adjacent subtitle");
                Flag(next, ValidationCode.Overlap, "Overlaps with adjacent subtitle");
            }
        }

        /// <summary>Computes the summary numbers (null when there are no candidates).</summary>
        public TimingRefinerStats ComputeStats()
        {
            // The title cards are not snapped: they are left out of the numbers
            var subs = Candidates.Where(c => c.Origin != TimingOrigin.TitleCard).ToList();
            if (subs.Count == 0)
                return null;

            var stats = new TimingRefinerStats
            {
                Total = subs.Count,
                TitleCards = Candidates.Count - subs.Count,
                Review = subs.Count(c => c.NeedsReview),
            };
            stats.Clean = stats.Total - stats.Review;

            foreach (var (code, _) in TimingRefinerStats.NoteCategories)
                stats.NoteCounts[code] = Candidates.Count(c => c.ValidationNotes.Any(n => n.Code == code));

            stats.AvgOriginalDuration = subs.Average(c => (c.OriginalEndTime - c.OriginalStartTime).TotalSeconds);
            stats.AvgFinalDuration = subs.Average(c => (c.End.Current - c.Start.Current).TotalSeconds);
            stats.AvgStartDrift = subs.Average(c => (c.Start.Current - c.OriginalStartTime).TotalSeconds);
            stats.MaxOutwardStartDrift = subs.Min(c => (c.Start.Current - c.OriginalStartTime).TotalSeconds);
            stats.AvgEndDrift = subs.Average(c => (c.End.Current - c.OriginalEndTime).TotalSeconds);
            stats.MaxOutwardEndDrift = subs.Max(c => (c.End.Current - c.OriginalEndTime).TotalSeconds);

            for (int i = 0; i < subs.Count; i++)
            {
                if (subs[i].End.Current - subs[i].Start.Current <= MinClipDuration)
                    stats.Squashed++;
                if (i < subs.Count - 1 && subs[i].End.Current > subs[i + 1].Start.Current)
                    stats.Overlaps++;
            }

            return stats;
        }
    }
}
