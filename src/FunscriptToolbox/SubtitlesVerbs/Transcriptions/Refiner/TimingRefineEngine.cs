using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    /// <summary>
    /// How the timings snapped on the terrain of the audio are refined (see <see cref="TimingRefinerSettings.Engine"/>): which questions
    /// are asked to the AI, and what is done with the answers. The engine is a configuration object (shared by all the runs: the main
    /// refiner, the parts of the split subtitles, the simulations): the state of a run is in the passes it creates for each refiner
    /// (<see cref="CreatePasses"/>).
    /// The refiner (<see cref="TimingRefiner"/>) holds what is common to all the engines: the subtitles (candidates), the terrain and the
    /// initial snap on it, the flags, the overlaps. The building blocks shared by the engines (the clips and their padding, the dips,
    /// the bleed text, the reading of the answers, the cache key) are here, for the engines to use.
    /// </summary>
    public abstract class TimingRefineEngine
    {
        /// <summary>Runaway guard only: the answers that a boundary can get (it converges in a few in practice).</summary>
        protected const int MaxAnswersPerBoundary = 10;

        /// <summary>The engine asks the AI (it needs an AI engine, and it costs tokens).</summary>
        [JsonIgnore]
        public abstract bool UsesAI { get; }

        /// <summary>The long subtitles can be split, and their parts refined (the parts have no text: it comes from what the AI heard).</summary>
        [JsonIgnore]
        public virtual bool CanSplitLongSubtitles => UsesAI;

        /// <summary>The simulations (TranscriberAIManualInputPreparer.Simulations) can replay this engine.</summary>
        [JsonIgnore]
        public virtual bool SupportsSimulations => false;

        /// <summary>The options of the requests (prompt, batching, extractors) that the engine uses (the data flow, the prerequisites).</summary>
        [JsonIgnore]
        public abstract IEnumerable<AIOptions> InnerOptionsUsed { get; }

        /// <summary>The passes of a run, in order (none: the boundaries stay as snapped on the terrain). New objects for each refiner.</summary>
        public abstract IReadOnlyList<TimingRefinePass> CreatePasses(TimingRefiner refiner);

        /// <summary>A clear error when something the engine needs is not configured.</summary>
        public virtual void Validate()
        {
        }

        // ---------------------------------------------------------------------
        // The cache of the answers (kept in the job state of the transcription, between the runs)
        // ---------------------------------------------------------------------

        /// <summary>
        /// The key of a request in the cache: the same key = the same request (the same audio and text sent), so its answer can be reused.
        /// (the padding is part of the key: the same clip with another padding is another audio; nothing is added without padding)
        /// </summary>
        public virtual string BuildCacheKey(TimingCandidate cand, TimingRound round)
        {
            var clipsSig = string.Join(";", round.Clips.Select(c => $"{c.OptionId}:{c.Start:hh\\:mm\\:ss\\.fff}-{c.End:hh\\:mm\\:ss\\.fff}{c.PaddingSignature}"));
            return $"{cand.OriginalStartTime:hh\\:mm\\:ss\\.fff}|{round.Side}|{round.Center:hh\\:mm\\:ss\\.fff}|{StableHash(round.TextSent)}|{clipsSig}";
        }

        /// <summary>64-bit FNV-1a (string.GetHashCode is randomized per process on modern .NET, so it can't be used for a persisted cache).</summary>
        protected static string StableHash(string text)
        {
            unchecked
            {
                ulong hash = 14695981039346656037;
                foreach (var ch in text ?? "")
                {
                    hash ^= ch;
                    hash *= 1099511628211;
                }
                return hash.ToString("x16");
            }
        }

        // ---------------------------------------------------------------------
        // Building blocks: the requests
        // ---------------------------------------------------------------------

        /// <summary>The text of a neighbour, when it's close enough that its voice could bleed into the clip (null otherwise).</summary>
        protected static string BleedTextFrom(TimingRefiner refiner, TimingCandidate neighbour, TimeSpan gap)
            => gap <= refiner.BleedDetectionMargin && !string.IsNullOrWhiteSpace(neighbour.TextForAI)
                ? neighbour.TextForAI
                : null;

        /// <summary>The text of the neighbour on the side asked (Start: the previous one, End: the next one), sent to the AI with the request.</summary>
        protected static void SetBleedTexts(TimingRefiner refiner, int index, BoundarySide side)
        {
            var candidates = refiner.Candidates;
            var cand = candidates[index];
            if (side == BoundarySide.Start)
            {
                cand.PossibleBleedFromPrev = index > 0
                    ? BleedTextFrom(refiner, candidates[index - 1], cand.Start.Current - candidates[index - 1].End.Current)
                    : null;
                cand.PossibleBleedFromNext = null;
            }
            else
            {
                cand.PossibleBleedFromPrev = null;
                cand.PossibleBleedFromNext = index < candidates.Count - 1
                    ? BleedTextFrom(refiner, candidates[index + 1], candidates[index + 1].Start.Current - cand.End.Current)
                    : null;
            }
        }

        /// <summary>
        /// Adds a clip to the round: Start side, the clip runs [boundaryTime .. clipAnchor]; End side, [clipAnchor .. boundaryTime].
        /// With 'padding', the clip has at least that much silence at its start (Start side) / its end (End side): see <see cref="AddClipPadding"/>.
        /// </summary>
        protected static TimingClipOption AddClip(
            TimingRefiner refiner, TimingRound round, BoundarySide side, string optionId, TimeSpan boundaryTime, TimeSpan clipAnchor, TimeSpan padding)
        {
            TimeSpan start = side == BoundarySide.Start ? boundaryTime : clipAnchor;
            TimeSpan end = side == BoundarySide.Start ? clipAnchor : boundaryTime;

            // Only possible for the initial boundary of a very short subtitle: never send an empty clip
            if (end - start < TimingRefiner.MinClipDuration)
            {
                if (side == BoundarySide.End)
                    end = refiner.ClampToAudioBounds(start + TimingRefiner.MinClipDuration);
                else
                    start = refiner.ClampToAudioBounds(end - TimingRefiner.MinClipDuration);
            }

            var clip = new TimingClipOption
            {
                FieldName = $"Audio_{optionId}",
                OptionId = optionId,
                Start = start,
                End = end
            };
            AddClipPadding(refiner, clip, side, padding);
            round.Clips.Add(clip);
            return clip;
        }

        /// <summary>A silence that is stolen is taken this far from its edges (the slopes of the speech next to it).</summary>
        private static readonly TimeSpan StolenSilenceMargin = TimeSpan.FromMilliseconds(50);

        /// <summary>How many silences, around the clip, are looked at to find one to steal from.</summary>
        private const int MaxSilencesLookedAt = 2000;

        /// <summary>A clean stretch of silence shorter than this is not worth splicing.</summary>
        private static readonly TimeSpan MinSplicedStretch = TimeSpan.FromMilliseconds(30);

        /// <summary>
        /// The clip must have at least 'padding' of silence at its start (Start side) / its end (End side): the silence that really
        /// precedes / follows the clip is used first, and the rest is stolen from the nearest other silence (spliced in front / after).
        /// Nothing is stolen when there is not enough silence anywhere (the padding is then shorter).
        /// </summary>
        protected static void AddClipPadding(TimingRefiner refiner, TimingClipOption clip, BoundarySide side, TimeSpan padding)
        {
            var terrain = refiner.TerrainMapping;
            if (padding <= TimeSpan.Zero || terrain == null || terrain.Segments.Count == 0)
                return;

            var segments = terrain.Segments;
            var oneMs = TimeSpan.FromMilliseconds(1);

            // The silence that really touches the clip
            int realIndex = side == BoundarySide.Start
                ? SegmentIndexAt(terrain, clip.Start - oneMs)
                : SegmentIndexAt(terrain, clip.End);
            (TimeSpan From, TimeSpan To)? real = null;
            if (realIndex >= 0 && segments[realIndex].IsSilence)
            {
                real = side == BoundarySide.Start
                    ? (TimeSpanExtensions.Max(segments[realIndex].StartTime, clip.Start - padding), clip.Start)
                    : (clip.End, TimeSpanExtensions.Min(segments[realIndex].EndTime, clip.End + padding));
                if (real.Value.To <= real.Value.From)
                    real = null;
            }

            var missing = padding - (real.HasValue ? real.Value.To - real.Value.From : TimeSpan.Zero);
            var stolen = missing > oneMs
                ? FindSilenceToSteal(terrain, missing, side == BoundarySide.Start ? clip.Start : clip.End, realIndex)
                : new List<(TimeSpan From, TimeSpan To)>();

            if (side == BoundarySide.Start)
            {
                clip.LeadingPieces.AddRange(stolen);
                if (real.HasValue) clip.LeadingPieces.Add(real.Value);
            }
            else
            {
                if (real.HasValue) clip.TrailingPieces.Add(real.Value);
                clip.TrailingPieces.AddRange(stolen);
            }
        }

        /// <summary>The index of the segment of the terrain that contains 'time' (-1 when before the first one).</summary>
        protected static int SegmentIndexAt(AudioTerrainMapping terrain, TimeSpan time)
        {
            var segments = terrain.Segments;
            int lo = 0, hi = segments.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (segments[mid].StartTime <= time) lo = mid + 1; else hi = mid;
            }
            return lo - 1;
        }

        /// <summary>
        /// Truly silent audio (never a digital silence, never a breath or a click, not even partly: they are merged in the Segments
        /// of the terrain, so the pure Silence pieces of DetailedSegments are used) taken from the silences nearest to 'near', not
        /// the one already used. When one stretch is long enough, 'length' is taken from its middle, away from the slopes of the speech.
        /// Otherwise the nearest clean stretches are spliced together until 'length' is reached (a silence can be spliced), so the
        /// padding is not shorter. Empty only when there is no clean silence at all.
        /// </summary>
        protected static List<(TimeSpan From, TimeSpan To)> FindSilenceToSteal(AudioTerrainMapping terrain, TimeSpan length, TimeSpan near, int excludedIndex)
        {
            var segments = terrain.Segments;
            int center = Math.Max(0, SegmentIndexAt(terrain, near));

            // The segments, nearest first
            var order = new List<int>();
            for (int step = 0; step < MaxSilencesLookedAt; step++)
            {
                if (center - step < 0 && center + step >= segments.Count)
                    break;
                if (center - step >= 0) order.Add(center - step);
                if (step > 0 && center + step < segments.Count) order.Add(center + step);
            }

            // The clean stretches (digital silence excluded) of a segment, with the margin kept on each side
            List<(TimeSpan From, TimeSpan To)> CleanStretches(int i)
            {
                var result = new List<(TimeSpan From, TimeSpan To)>();
                if (i < 0 || i >= segments.Count || i == excludedIndex)
                    return result;
                var seg = segments[i];
                if (!seg.IsSilence || seg.PeakLevel <= 0f)
                    return result;

                var detailed = terrain.DetailedSegments;
                int lo = 0, hi = detailed.Count;
                while (lo < hi)
                {
                    int mid = (lo + hi) / 2;
                    if (detailed[mid].EndTime <= seg.StartTime) lo = mid + 1; else hi = mid;
                }
                for (int k = lo; k < detailed.Count && detailed[k].StartTime < seg.EndTime; k++)
                {
                    var piece = detailed[k];
                    if (!piece.IsSilence || piece.PeakLevel <= 0f)
                        continue;
                    var from = TimeSpanExtensions.Max(piece.StartTime, seg.StartTime) + StolenSilenceMargin;
                    var to = TimeSpanExtensions.Min(piece.EndTime, seg.EndTime) - StolenSilenceMargin;
                    if (to - from >= MinSplicedStretch)
                        result.Add((from, to));
                }
                return result;
            }

            // 1. One stretch that is long enough (the longest of the nearest segment that has one)
            foreach (var i in order)
            {
                var stretches = CleanStretches(i);
                if (stretches.Count == 0)
                    continue;
                var best = stretches.OrderByDescending(x => x.To - x.From).First();
                if (best.To - best.From >= length)
                {
                    var from = best.From + TimeSpan.FromTicks((best.To - best.From - length).Ticks / 2);
                    return new List<(TimeSpan From, TimeSpan To)> { (from, from + length) };
                }
            }

            // 2. Not one is long enough: splice the nearest clean stretches (each taken from its middle)
            var pieces = new List<(TimeSpan From, TimeSpan To)>();
            var remaining = length;
            foreach (var i in order)
            {
                foreach (var stretch in CleanStretches(i).OrderBy(x => (x.From - near).Duration()))
                {
                    var take = TimeSpanExtensions.Min(stretch.To - stretch.From, remaining);
                    var from = stretch.From + TimeSpan.FromTicks((stretch.To - stretch.From - take).Ticks / 2);
                    pieces.Add((from, from + take));
                    remaining -= take;
                    if (remaining <= TimeSpan.FromMilliseconds(1))
                        return pieces;
                }
            }
            return pieces;
        }

        // ---------------------------------------------------------------------
        // Building blocks: the dips (the possible places of a boundary, on the terrain)
        // ---------------------------------------------------------------------

        protected static void EnsureDipPresent(List<BoundaryDipCandidate> list, TimeSpan time)
        {
            if (!list.Any(d => d.Time == time))
            {
                list.Add(new BoundaryDipCandidate(time));
                list.Sort((a, b) => a.Time.CompareTo(b.Time));
            }
        }

        /// <summary>The dips nearest to 'center' (at most maxInward inside, maxOutward outside); firstDroppedOutward: the first dip left out outside.</summary>
        protected static IEnumerable<TimeSpan> LimitDipsAround(
            IEnumerable<TimeSpan> dips, TimeSpan center, BoundarySide side,
            int? maxInward, int? maxOutward, out TimeSpan? firstDroppedOutward)
        {
            var list = dips.OrderBy(t => t).ToList();
            bool isStart = side == BoundarySide.Start;

            // Nearest first
            var inward = (isStart ? list.Where(t => t > center).OrderBy(t => t)
                                : list.Where(t => t < center).OrderByDescending(t => t)).ToList();
            var outward = (isStart ? list.Where(t => t < center).OrderByDescending(t => t)
                                : list.Where(t => t > center).OrderBy(t => t)).ToList();

            firstDroppedOutward = maxOutward.HasValue && outward.Count > maxOutward.Value
                ? outward[maxOutward.Value]
                : (TimeSpan?)null;

            return inward.Take(maxInward ?? int.MaxValue)
                .Concat(list.Where(t => t == center))
                .Concat(outward.Take(maxOutward ?? int.MaxValue))
                .OrderBy(t => t)
                .ToList();
        }

        /// <summary>
        /// Picks 'count' items spread evenly over the sorted list (all of them when the list is not longer than 'count').
        /// The list is cut in 'count' * 2 sections and the boundaries 1, 3, 5, ... are used (the first and last sections are half size).
        /// Ex: 15 items, count = 4 => the 2nd, 6th, 10th and 14th.
        /// </summary>
        protected static List<TimeSpan> SampleEvenly(List<TimeSpan> sorted, int count)
        {
            if (sorted.Count <= count)
                return sorted;

            var result = new List<TimeSpan>();
            for (int j = 1; j <= count; j++)
            {
                var index = (int)Math.Floor((2 * j - 1) * sorted.Count / (2.0 * count));
                result.Add(sorted[Math.Min(Math.Max(index, 0), sorted.Count - 1)]);
            }
            return result.Distinct().ToList();
        }

        protected static void MarkDipSelected(TimingCandidate cand, BoundarySide side, TimeSpan resolved)
        {
            foreach (var d in cand[side].Dips)
            {
                if (d.Time == resolved && d.State != DipCandidateState.HardLimit)
                    d.State = DipCandidateState.Selected;
            }
        }

        /// <summary>Eliminates every dip at or inside 'boundaryTime' (later for Start, earlier for End).</summary>
        protected static void EliminateDips(TimingCandidate cand, BoundarySide side, TimeSpan boundaryTime)
        {
            bool isStart = side == BoundarySide.Start;
            foreach (var d in cand[side].Dips)
            {
                if (d.State == DipCandidateState.HardLimit)
                    continue;

                bool shouldEliminate = isStart ? d.Time >= boundaryTime : d.Time <= boundaryTime;
                if (shouldEliminate && d.State != DipCandidateState.Selected)
                    d.State = DipCandidateState.Eliminated;
            }
        }

        protected static TimeSpan? GetHardLimit(TimingCandidate cand, BoundarySide side)
        {
            var hardDips = cand[side].Dips.Where(d => d.State == DipCandidateState.HardLimit).ToList();
            if (hardDips.Count == 0) return null;

            bool isStart = side == BoundarySide.Start;
            if (isStart)
            {
                var firstForbidden = hardDips.Max(d => d.Time);
                var allowed = cand[side].Dips.Where(d => d.State != DipCandidateState.HardLimit && d.Time >= firstForbidden).ToList();
                return allowed.Count > 0 ? allowed.Min(d => d.Time) : firstForbidden;
            }
            else
            {
                var firstForbidden = hardDips.Min(d => d.Time);
                var allowed = cand[side].Dips.Where(d => d.State != DipCandidateState.HardLimit && d.Time <= firstForbidden).ToList();
                return allowed.Count > 0 ? allowed.Max(d => d.Time) : firstForbidden;
            }
        }

        /// <summary>Forbids every dip beyond 'limit' (earlier for Start, later for End).</summary>
        protected static void SetHardLimit(TimingCandidate cand, BoundarySide side, TimeSpan limit, bool ensurePresent)
        {
            if (ensurePresent)
                EnsureDipPresent(cand[side].Dips, limit);

            bool isStart = side == BoundarySide.Start;
            foreach (var d in cand[side].Dips)
            {
                if (isStart ? d.Time < limit : d.Time > limit)
                    d.State = DipCandidateState.HardLimit;
            }
        }

        /// <summary>The dips that were in play when the round was built, with their state after the AI's answer.</summary>
        protected static string FormatDips(TimingCandidate cand, BoundarySide side, TimingRound round)
        {
            var initTime = cand[side].Init;
            return string.Join(" ", cand[side].Dips
                .Where(d => round.DipTimesAtStart.Contains(d.Time))
                .Select(d => d.Format(initTime)));
        }

        // ---------------------------------------------------------------------
        // Building blocks: the answers
        // ---------------------------------------------------------------------

        protected static string GetTrimmedMeta(TranscribedItem item, string key)
        {
            item.Metadata.TryGetValue(key, out var value);
            return value?.Trim();
        }

        protected static bool IsInaudibleToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return false;
            return token.IndexOf("Inaudible", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Accepts "S2", "Max", "Audio_S2" (the clip field name) and "Audio_Max". Strips a trailing '*'.</summary>
        protected static string NormalizeToken(string rawChoice)
        {
            string token = (rawChoice ?? "").Trim();

            const string fieldPrefix = "Audio_";
            if (token.StartsWith(fieldPrefix, StringComparison.OrdinalIgnoreCase))
                token = token.Substring(fieldPrefix.Length);

            return token.TrimEnd('*').Trim();
        }

        private static char SideChar(BoundarySide side) => side == BoundarySide.Start ? 'S' : 'E';

        /// <summary>
        /// The answer of the AI to the round of a candidate (on 'side'): its choice (metadata 'choiceMetadata') and what it heard.
        /// Common to the engines: "Inaudible" locks the subtitle (flagged); otherwise 'applyChoice' moves / locks the boundary;
        /// boundaries that cross are put back; what was heard, when it's not the text sent, is kept (Start side: it's the text of the
        /// next requests; a split part has no text of its own: it's its text) or flagged; with 'trimSilence', the boundary, once locked,
        /// is moved inward over the silence (see TimingRefinerSettings.TrimSilenceOnStart / End).
        /// </summary>
        protected static void ApplyAnswer(
            TimingRefiner refiner,
            TimingCandidate cand,
            TranscribedItem item,
            BoundarySide side,
            string choiceMetadata,
            Action<TimingCandidate, TimingRound> applyChoice,
            bool trimSilence)
        {
            var round = cand.CurrentRound;
            var boundary = cand[side];
            boundary.RetryCount++;

            round.Choice = GetTrimmedMeta(item, choiceMetadata);
            round.HeardText = GetTrimmedMeta(item, TimingMeta.HeardText);
            cand.Rounds.Add(round);
            cand.CurrentRound = null;

            var step = $"#{boundary.RetryCount}{SideChar(side)}";
            var prevStart = cand.Start.Current;
            var prevEnd = cand.End.Current;

            if (IsInaudibleToken(round.Choice))
            {
                cand.LockAll();
                refiner.Flag(cand, ValidationCode.Inaudible, $"Audio Inaudible (original): {cand.OriginalText}");
                cand.Trace(step, prevStart, prevEnd, "AI: Inaudible", round.HeardText);
                return;
            }

            applyChoice(cand, round);

            // Guard against crossed boundaries
            bool crossed = side == BoundarySide.Start
                ? cand.Start.Current >= cand.End.Current
                : cand.End.Current <= cand.Start.Current;
            if (crossed)
            {
                // (no silence trimming: the previous timing is kept as is)
                boundary.Current = round.Center;
                boundary.Locked = true;
                refiner.Flag(cand, ValidationCode.BoundariesCrossed, $"Boundaries crossed ({side}), kept previous timing");
            }

            // HeardText: what the AI actually heard when it disagrees with the text it was given
            bool hasHeard = !string.IsNullOrWhiteSpace(round.HeardText)
                && !string.Equals(round.HeardText, "null", StringComparison.OrdinalIgnoreCase);
            if (hasHeard && !refiner.IsSameText(round.TextSent, round.HeardText))
            {
                // The End pass is judged on what was heard in the Start pass
                if (side == BoundarySide.Start)
                    cand.HeardText = round.HeardText;

                if (side == BoundarySide.Start && cand.Origin != TimingOrigin.Original)
                {
                    // A split part has no text of its own: what the Start pass heard IS its text (not a mismatch)
                    cand.Trace("Text", cand.Start.Current, cand.End.Current, "Text given by what was heard", round.HeardText);
                }
                else
                {
                    refiner.Flag(cand, ValidationCode.TextMismatch, side == BoundarySide.Start
                        ? $"Text Mismatch (original vs heard in Max): `{cand.OriginalText}` vs `{round.HeardText}`"
                        : $"Text Mismatch (start-pass text vs heard in End pass): `{round.TextSent}` vs `{round.HeardText}`");
                }
            }

            cand.Traces.Add($"{step} {side} options:");
            cand.Traces.Add($"     {FormatDips(cand, side, round)}");
            cand.Trace(step, prevStart, prevEnd, $"AI: {round.Choice}", round.HeardText);

            if (trimSilence && !crossed && boundary.Locked)
                refiner.TrimSilence(cand, side);
        }

        /// <summary>After the last round of a pass on 'side': whatever is still unlocked is locked and flagged.</summary>
        protected static void LockUnfinished(TimingRefiner refiner, BoundarySide side)
        {
            foreach (var cand in refiner.Candidates)
            {
                cand.CurrentRound = null;

                var boundary = cand[side];
                if (boundary.Locked)
                    continue;

                boundary.Locked = true;
                refiner.Flag(cand, ValidationCode.Unfinished, $"Unfinished {side} (safety limit reached)");
                cand.Trace("Fin", cand.Start.Current, cand.End.Current, $"{side} left unfinished");
            }
        }
    }

    /// <summary>
    /// A pass of an engine: one kind of request (the side of the boundary asked, the options of the requests, the metadata of the answer),
    /// asked again and again until it has nothing left to ask. Created for each refiner by <see cref="TimingRefineEngine.CreatePasses"/>
    /// (it holds the state of the run). The caller: Begin, then PrepareNextRound / send the requests of the candidates whose CurrentRound
    /// is set / ProcessResults, until no candidate has a CurrentRound, then End.
    /// </summary>
    public abstract class TimingRefinePass
    {
        protected TimingRefinePass(
            TimingRefineEngine engine,
            TimingRefiner refiner,
            string name,
            BoundarySide side,
            AIOptions options,
            string metadataProduced,
            bool isTailCheck = false)
        {
            this.Engine = engine;
            this.Refiner = refiner;
            this.Name = name;
            this.Side = side;
            this.Options = options;
            this.MetadataProduced = metadataProduced;
            this.IsTailCheck = isTailCheck;
        }

        public TimingRefineEngine Engine { get; }
        public TimingRefiner Refiner { get; }

        /// <summary>How the pass is named in the progress and the log (ex. "Start", "End", "End tail", "Verify").</summary>
        public string Name { get; }

        /// <summary>The side of the boundary that is asked (the bleed text sent with the request follows it: Start = the previous subtitle, End = the next one).</summary>
        public BoundarySide Side { get; }

        /// <summary>The options of the requests: prompt, batching, extractors.</summary>
        public AIOptions Options { get; }

        /// <summary>The metadata of the answer (when the options don't say another one).</summary>
        public string MetadataProduced { get; }

        /// <summary>A check of the boundaries already chosen (the End tail check): not a pass of its own side (no 'unasked' side).</summary>
        public bool IsTailCheck { get; }

        /// <summary>The metadata of the answers that are kept in the cache (what ProcessResults reads).</summary>
        public virtual IEnumerable<string> CachedMetadataNames => new[] { this.MetadataProduced, TimingMeta.HeardText };

        public virtual void Begin()
        {
        }

        /// <summary>Sets the round to ask (CurrentRound) on every candidate that has something to ask (null on the others).</summary>
        public abstract void PrepareNextRound();

        /// <summary>The answers of the AI (one item per candidate asked, at its original start time).</summary>
        public abstract void ProcessResults(IEnumerable<TranscribedItem> responseItems);

        public virtual void End()
        {
        }
    }
}
