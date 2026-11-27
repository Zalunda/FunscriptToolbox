using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    /// <summary>How a boundary is picked when the runs don't give the same answer.</summary>
    public enum SpokenMarkersPick
    {
        /// <summary>The widest of the answers (the earliest Start, the latest End): extra silence is better than a cut.</summary>
        Wider,

        /// <summary>The answer given by the most runs (a tie: the widest of the tied ones). Needs at least 3 runs.</summary>
        Majority,
    }

    /// <summary>
    /// After the initial snap: the "spoken markers" (see scratchbook/README.md, tests T07 to T16).
    /// For each subtitle, short English number words (<see cref="SpokenMarkers"/>, a random label each) are INSERTED in its audio at the
    /// possible places of its Start (the start dips of the terrain around the snapped Start) and of its End (the end dips around the
    /// snapped End). The AI gets the clip and the text of the line, and transcribes everything it hears with the markers inline and
    /// the line between « and ». The Start is the last marker written before «, the End the first marker after ».
    ///
    /// ONE pass, asked 'Runs' times (each run: other random labels, so the runs are more independent): the boundaries are decided once,
    /// after the last run (OnDisagreement), then the silence is trimmed (TimingRefinerSettings.TrimSilenceOnStart / End) and each End is
    /// capped by the next Start. The confidence (both runs gave the same marker, nothing written between it and « / ») is written as a
    /// '[TimingConfidence:x] reason' line (the 'Conf.' column of the Timing editor).
    /// The long subtitles are not split with this engine (a split part has no text to give).
    /// </summary>
    [ConfigDoc("Short English number words are inserted in the audio of each subtitle at the possible places of its start and its end (the dips of the terrain); " +
        "the AI transcribes the clip with the markers inline and the line between « and »: the markers around the line give its start and its end. " +
        "One request per subtitle and per run, in batches. A confidence per subtitle (the 'Conf.' column of the Review editor). The long subtitles are not split with this engine.",
        Advice = "The AI engine of the step needs a \"maxOutputTokens\" big enough for a whole batch of answers (ex. 16384 for 50) and, ideally, a low thinking level.")]
    public class TimingRefineEngineSpokenMarkers : TimingRefineEngine
    {
        [ConfigDoc("The options of the request (prompt, batching, ONE clip with the markers: 'AudioClip'): it must produce \"MarkedTranscription\". Defined in the main configuration.")]
        [JsonProperty(Order = 1)]
        public AIOptions InnerOptions { get; set; }

        [ConfigDoc("How many times each subtitle is asked (other random labels each time). 1 = no cross-check (no real confidence). " +
            "When the runs agree, the boundary gets a high confidence; otherwise OnDisagreement decides.",
            Advice = "2: the best value in the tests.")]
        [JsonProperty(Order = 2)]
        public int Runs { get; set; } = 2;

        [ConfigDoc("How a boundary is picked when the runs disagree: Wider (the widest answer: extra silence is better than a cut) or Majority (the answer of the most runs; needs Runs >= 3).")]
        [JsonProperty(Order = 3)]
        public SpokenMarkersPick OnDisagreement { get; set; } = SpokenMarkersPick.Wider;

        [ConfigDoc("How many dips before (earlier than) the snapped Start get a marker. At most 12 markers in total for a subtitle (the number of labels).")]
        [JsonProperty(Order = 4)]
        public int StartOptionsBefore { get; set; } = 2;

        [ConfigDoc("How many dips after the snapped Start get a marker.")]
        [JsonProperty(Order = 5)]
        public int StartOptionsAfter { get; set; } = 2;

        [ConfigDoc("How many dips before the snapped End get a marker.",
            Advice = "The snapped Ends are often too wide: more options before the End than after.")]
        [JsonProperty(Order = 6)]
        public int EndOptionsBefore { get; set; } = 4;

        [ConfigDoc("How many dips after the snapped End get a marker.")]
        [JsonProperty(Order = 7)]
        public int EndOptionsAfter { get; set; } = 2;

        [ConfigDoc("How far from the snapped boundary a marker can be.")]
        [JsonProperty(Order = 8)]
        public TimeSpan MaxExpansion { get; set; } = TimeSpan.FromSeconds(2.0);

        [ConfigDoc("Two markers closer than this are one marker (the time of the snapped boundary is kept when it's one of them).")]
        [JsonProperty(Order = 9)]
        public TimeSpan MarkerMergeDistance { get; set; } = TimeSpan.FromMilliseconds(80);

        [ConfigDoc("The audio kept before the first marker and after the last one.")]
        [JsonProperty(Order = 10)]
        public TimeSpan ClipMargin { get; set; } = TimeSpan.FromSeconds(0.7);

        public override bool UsesAI => true;

        /// <summary>A split part has no text of its own: it can't be asked with this engine.</summary>
        public override bool CanSplitLongSubtitles => false;

        public override IEnumerable<AIOptions> InnerOptionsUsed => new[] { this.InnerOptions };

        public override void Validate()
        {
            if (this.InnerOptions == null)
                throw new InvalidOperationException("The refine engine 'SpokenMarkers' needs 'InnerOptions' (the options of its request).");
            if (this.Runs < 1)
                throw new InvalidOperationException("The refine engine 'SpokenMarkers': 'Runs' must be at least 1.");
            if (this.OnDisagreement == SpokenMarkersPick.Majority && this.Runs < 3)
                throw new InvalidOperationException("The refine engine 'SpokenMarkers': 'OnDisagreement' = 'Majority' needs at least 3 'Runs'.");
            if (this.StartOptionsBefore < 0 || this.StartOptionsAfter < 0 || this.EndOptionsBefore < 0 || this.EndOptionsAfter < 0)
                throw new InvalidOperationException("The refine engine 'SpokenMarkers': the numbers of options can't be negative.");
            var maxMarkers = this.StartOptionsBefore + this.StartOptionsAfter + 1 + this.EndOptionsBefore + this.EndOptionsAfter + 1;
            if (maxMarkers > SpokenMarkers.Labels.Count)
                throw new InvalidOperationException(
                    $"The refine engine 'SpokenMarkers': up to {maxMarkers} markers per clip with these options, but there are only {SpokenMarkers.Labels.Count} labels " +
                    "(StartOptionsBefore + StartOptionsAfter + EndOptionsBefore + EndOptionsAfter + 2 must not be more).");
            if (this.MaxExpansion <= TimeSpan.Zero || this.MarkerMergeDistance < TimeSpan.Zero || this.ClipMargin < TimeSpan.Zero)
                throw new InvalidOperationException("The refine engine 'SpokenMarkers': 'MaxExpansion' must be positive, 'MarkerMergeDistance' and 'ClipMargin' can't be negative.");
            foreach (var extractor in this.InnerOptions.BinaryDataExtractors?.OfType<BinaryDataExtractorAudio>() ?? Enumerable.Empty<BinaryDataExtractorAudio>())
            {
                if (extractor.ExpandStart != TimeSpan.Zero || extractor.ExpandEnd != TimeSpan.Zero || extractor.FillGapSmallerThen != TimeSpan.Zero)
                    throw new InvalidOperationException(
                        $"The refine engine 'SpokenMarkers': its audio extractor '{extractor.OutputFieldName}' has ExpandStart, ExpandEnd or FillGapSmallerThen set: " +
                        "the clip is built by the engine (set them to 0 and use ClipMargin).");
            }
        }

        public override IReadOnlyList<TimingRefinePass> CreatePasses(TimingRefiner refiner)
            => new TimingRefinePass[] { new MarkersPass(this, refiner) };

        // =====================================================================
        // Reading an answer
        // =====================================================================

        /// <summary>What one run says about one boundary: the time of the marker (null: none), and whether it is 'tight' (nothing written between it and « / »).</summary>
        internal class SideReading
        {
            public TimeSpan? Time;
            public bool Tight;
            public string Label;
            public string How;

            /// <summary>The boundary is beyond every marker (« before the first one, » after the last one): further than the options.</summary>
            public bool Beyond;

            public static SideReading None(string how) => new SideReading { How = how };
            public static SideReading BeyondAll(string how) => new SideReading { How = how, Beyond = true };
        }

        internal class Reading
        {
            public SideReading Start;
            public SideReading End;
            public string Raw;

            public SideReading this[BoundarySide side] => side == BoundarySide.Start ? Start : End;

            public static Reading NoAnswer(string how) => new Reading { Start = SideReading.None(how), End = SideReading.None(how) };
        }

        private static readonly Regex rs_tokens = new Regex(@"\[(\d+)\]|«|»", RegexOptions.Compiled);
        private static readonly Regex rs_markerOnly = new Regex(@"\[\d+\]", RegexOptions.Compiled);

        /// <summary>What can be written between a marker and « / » and still be 'tight' (spaces, punctuation, the long vowel mark).</summary>
        private static readonly Regex rs_junk = new Regex(@"[\s、。，．,.!?！？…ー〜~・「」\-]+", RegexOptions.Compiled);

        private static bool IsJunkOnly(string text) => rs_junk.Replace(rs_markerOnly.Replace(text ?? "", ""), "").Length == 0;

        /// <summary>
        /// The answer of one run (the transcription with the markers inline, the line between « and »), read with the markers of the round
        /// (Options: the number of the label => the time of the marker). A number that is not one of the markers (invented) is ignored.
        ///   Start = the last marker written before the first «; but "« [m] text" (nothing of the line before m) = the line starts AT m.
        ///   End   = the first marker written after the last ».
        /// (The "« [m]" rule is not used for the End: "...しょ。 [m] »" happens when the last vowel goes on after m without new text.)
        /// </summary>
        internal static Reading Read(string text, IReadOnlyDictionary<string, TimeSpan> options)
        {
            if (string.IsNullOrWhiteSpace(text))
                return Reading.NoAnswer("no answer");

            var tokens = rs_tokens.Matches(text).Cast<Match>().ToList();
            bool IsKnown(int i) => i >= 0 && i < tokens.Count && tokens[i].Groups[1].Success && options.ContainsKey(tokens[i].Groups[1].Value);
            SideReading At(int i, bool tight, string how) => new SideReading
            {
                Time = options[tokens[i].Groups[1].Value],
                Tight = tight,
                Label = tokens[i].Groups[1].Value,
                How = how,
            };

            var reading = new Reading { Raw = text };

            // Start
            int open = tokens.FindIndex(t => t.Value == "«");
            if (open < 0)
            {
                reading.Start = SideReading.None("no «");
            }
            else
            {
                int inner = open + 1;
                if (IsKnown(inner) && IsJunkOnly(text.Substring(tokens[open].Index + 1, tokens[inner].Index - tokens[open].Index - 1)))
                {
                    reading.Start = At(inner, true, "tight (marker inside « »)");
                }
                else
                {
                    int j = open - 1;
                    while (j >= 0 && !IsKnown(j)) j--;
                    if (j < 0)
                    {
                        reading.Start = SideReading.BeyondAll("« before every marker");
                    }
                    else
                    {
                        var between = text.Substring(tokens[j].Index + tokens[j].Length, tokens[open].Index - tokens[j].Index - tokens[j].Length);
                        bool tight = IsJunkOnly(between);
                        reading.Start = At(j, tight, tight ? "tight" : $"loose ({between.Trim()})");
                    }
                }
            }

            // End
            int close = tokens.FindLastIndex(t => t.Value == "»");
            if (close < 0)
            {
                reading.End = SideReading.None("no »");
            }
            else
            {
                int j = close + 1;
                while (j < tokens.Count && !IsKnown(j)) j++;
                if (j >= tokens.Count)
                {
                    reading.End = SideReading.BeyondAll("» after every marker");
                }
                else
                {
                    var between = text.Substring(tokens[close].Index + 1, tokens[j].Index - tokens[close].Index - 1);
                    bool tight = IsJunkOnly(between);
                    reading.End = At(j, tight, tight ? "tight" : $"loose ({between.Trim()})");
                }
            }
            return reading;
        }

        // =====================================================================
        // The pass
        // =====================================================================

        private class Marker
        {
            public TimeSpan Time;
            public readonly List<string> Tags = new List<string>();
        }

        private class CandidateState
        {
            public List<Marker> Markers;
            public readonly List<Reading> Readings = new List<Reading>();
            public bool Decided;
            /// <summary>The snapped timing (before the runs): where a subtitle placed inside its neighbour goes back to (see RevertInsideNeighbours).</summary>
            public TimeSpan SnappedStart;
            public TimeSpan SnappedEnd;
        }

        private class MarkersPass : TimingRefinePass
        {
            private readonly TimingRefineEngineSpokenMarkers r_engine;
            private readonly Dictionary<TimingCandidate, CandidateState> r_states = new Dictionary<TimingCandidate, CandidateState>();

            public MarkersPass(TimingRefineEngineSpokenMarkers engine, TimingRefiner refiner)
                : base(engine, refiner, "Spoken markers", BoundarySide.Start, engine.InnerOptions, TimingMeta.MarkedTranscription)
            {
                r_engine = engine;
            }

            public override IEnumerable<string> CachedMetadataNames => new[] { TimingMeta.MarkedTranscription };

            /// <summary>The markers of every candidate that can be asked (the ones without text are kept as snapped).</summary>
            public override void Begin()
            {
                var candidates = this.Refiner.Candidates;
                for (int i = 0; i < candidates.Count; i++)
                {
                    var cand = candidates[i];
                    if (cand.Origin == TimingOrigin.TitleCard || cand.IsFullyLocked)
                        continue;

                    if (string.IsNullOrWhiteSpace(cand.OriginalText))
                    {
                        cand.LockAll();
                        cand.Trace("Mrk", cand.Start.Current, cand.End.Current, "No text: kept as snapped");
                        continue;
                    }

                    // (no bleed text: the requests of the tests had only the line's text)
                    cand.PossibleBleedFromPrev = null;
                    cand.PossibleBleedFromNext = null;

                    var markers = BuildMarkers(cand, i > 0 ? candidates[i - 1] : null, i + 1 < candidates.Count ? candidates[i + 1] : null);
                    r_states[cand] = new CandidateState { Markers = markers, SnappedStart = cand.Start.Current, SnappedEnd = cand.End.Current };

                    // The options of each side are its dips (for the summary: how many dips the final boundary is from the snapped one)
                    foreach (var m in markers)
                    {
                        if (m.Tags.Any(t => t[0] == 'S'))
                            EnsureDipPresent(cand.Start.Dips, m.Time);
                        if (m.Tags.Any(t => t[0] == 'E'))
                            EnsureDipPresent(cand.End.Dips, m.Time);
                    }
                    cand.Traces.Add($"Mrk  markers: {string.Join(" ", markers.Select(m => $"{m.Time:mm\\:ss\\.fff}({string.Join("/", m.Tags)})"))}");
                }
            }

            /// <summary>
            /// The start dips around the snapped Start (StartOptionsBefore / After) and the end dips around the snapped End (EndOptionsBefore / After),
            /// within MaxExpansion, a Start never before the previous subtitle's snapped Start nor after its own End, an End never after the next
            /// subtitle's snapped End nor before its own Start. Merged when closer than MarkerMergeDistance. Tags: S0 / E0 = the snapped boundary,
            /// S-1 = the first option before it, S+1 = the first after it...
            /// </summary>
            private List<Marker> BuildMarkers(TimingCandidate cand, TimingCandidate prev, TimingCandidate next)
            {
                var terrain = this.Refiner.TerrainMapping;
                var s0 = cand.Start.Current;
                var e0 = cand.End.Current;
                var tick = TimeSpan.FromTicks(1);

                var startLo = this.Refiner.ClampToAudioBounds(s0 - r_engine.MaxExpansion);
                if (prev != null && prev.Start.Init + tick > startLo)
                    startLo = prev.Start.Init + tick;
                var startHi = TimeSpanMin(s0 + r_engine.MaxExpansion, e0 - TimingRefiner.MinClipDuration);
                var startDips = terrain.GetDips(BoundarySide.Start, startLo, startHi).Where(t => t >= startLo && t <= startHi).Append(s0).Distinct();
                var starts = LimitDipsAround(startDips, s0, BoundarySide.Start, r_engine.StartOptionsAfter, r_engine.StartOptionsBefore, out _);

                var endLo = TimeSpanMax(e0 - r_engine.MaxExpansion, s0 + TimingRefiner.MinClipDuration);
                var endHi = this.Refiner.ClampToAudioBounds(e0 + r_engine.MaxExpansion);
                if (next != null && next.End.Init - tick < endHi)
                    endHi = next.End.Init - tick;
                var endDips = terrain.GetDips(BoundarySide.End, endLo, endHi).Where(t => t >= endLo && t <= endHi).Append(e0).Distinct();
                var ends = LimitDipsAround(endDips, e0, BoundarySide.End, r_engine.EndOptionsBefore, r_engine.EndOptionsAfter, out _);

                var tagged = new SortedDictionary<TimeSpan, Marker>();
                void AddAll(IEnumerable<TimeSpan> times, TimeSpan center, char side)
                {
                    var list = times.OrderBy(t => t).ToList();
                    int c = list.IndexOf(center);
                    for (int k = 0; k < list.Count; k++)
                    {
                        if (!tagged.TryGetValue(list[k], out var m))
                            tagged[list[k]] = m = new Marker { Time = list[k] };
                        m.Tags.Add(k == c ? $"{side}0" : $"{side}{k - c:+0;-0}");
                    }
                }
                AddAll(starts, s0, 'S');
                AddAll(ends, e0, 'E');

                // Merge the markers closer than MarkerMergeDistance (the time of S0 / E0 wins, otherwise the earlier one)
                bool IsInit(Marker m) => m.Tags.Contains("S0") || m.Tags.Contains("E0");
                var merged = new List<Marker>();
                foreach (var m in tagged.Values)
                {
                    var last = merged.LastOrDefault();
                    if (last != null && m.Time - last.Time < r_engine.MarkerMergeDistance)
                    {
                        if (IsInit(m) && !IsInit(last))
                            last.Time = m.Time;
                        last.Tags.AddRange(m.Tags);
                    }
                    else
                    {
                        merged.Add(m);
                    }
                }
                return merged;
            }

            /// <summary>
            /// Each candidate is asked once per run (one iteration = one run for all of them). A candidate asked and not answered gets a
            /// "no answer" for that run (it is not asked again). After the last run, its boundaries are decided.
            /// </summary>
            public override void PrepareNextRound()
            {
                foreach (var cand in this.Refiner.Candidates)
                {
                    if (!r_states.TryGetValue(cand, out var state) || state.Decided)
                    {
                        cand.CurrentRound = null;
                        continue;
                    }

                    if (cand.CurrentRound != null)
                    {
                        state.Readings.Add(Reading.NoAnswer("no answer"));
                        cand.Rounds.Add(cand.CurrentRound);
                        cand.Trace($"#{cand.CurrentRound.RoundNumber}M", cand.Start.Current, cand.End.Current, "No answer from the AI");
                        cand.CurrentRound = null;
                    }

                    if (state.Readings.Count >= r_engine.Runs)
                    {
                        Decide(cand, state);
                        continue;
                    }

                    cand.CurrentRound = BuildRound(cand, state, state.Readings.Count + 1);
                }
            }

            /// <summary>ONE clip: [first marker - ClipMargin, last marker + ClipMargin] with a random label inserted at each marker.</summary>
            private TimingRound BuildRound(TimingCandidate cand, CandidateState state, int run)
            {
                var labels = ShuffledLabels(cand, run);
                var round = new TimingRound
                {
                    Side = BoundarySide.Start,
                    RoundNumber = run,
                    Center = cand.Start.Current,
                    ClipAnchor = cand.End.Current,
                    TextSent = cand.OriginalText,
                };
                var clip = new TimingClipOption
                {
                    FieldName = "AudioClip",
                    OptionId = $"Run{run}",
                    Start = this.Refiner.ClampToAudioBounds(state.Markers[0].Time - r_engine.ClipMargin),
                    End = this.Refiner.ClampToAudioBounds(state.Markers[state.Markers.Count - 1].Time + r_engine.ClipMargin),
                };
                for (int k = 0; k < state.Markers.Count; k++)
                {
                    var (word, number) = labels[k];
                    clip.Inserts.Add(new TimingClipInsert { At = state.Markers[k].Time, Label = word });
                    round.Options[number.ToString(CultureInfo.InvariantCulture)] = state.Markers[k].Time;
                    round.DipTimesAtStart.Add(state.Markers[k].Time);
                }
                round.Clips.Add(clip);
                return round;
            }

            /// <summary>The labels in a random order, always the same for a subtitle and a run (the request can be found in the cache again).</summary>
            private static List<(string Word, int Number)> ShuffledLabels(TimingCandidate cand, int run)
            {
                var hash = ulong.Parse(StableHash($"{cand.OriginalStartTime:hh\\:mm\\:ss\\.fff}|{run}"), NumberStyles.HexNumber);
                var random = new Random((int)(hash ^ (hash >> 32)));
                var labels = SpokenMarkers.Labels.ToList();
                for (int i = labels.Count - 1; i > 0; i--)
                {
                    int j = random.Next(i + 1);
                    (labels[i], labels[j]) = (labels[j], labels[i]);
                }
                return labels;
            }

            public override void ProcessResults(IEnumerable<TranscribedItem> responseItems)
            {
                foreach (var item in responseItems)
                {
                    var cand = this.Refiner.FindCandidateFor(item.StartTime, c => c.CurrentRound != null && r_states.ContainsKey(c));
                    if (cand == null)
                        continue;

                    var round = cand.CurrentRound;
                    var text = GetTrimmedMeta(item, TimingMeta.MarkedTranscription);
                    round.Choice = text;
                    var reading = Read(text, round.Options);
                    r_states[cand].Readings.Add(reading);
                    cand.Rounds.Add(round);
                    cand.CurrentRound = null;
                    cand.Trace($"#{round.RoundNumber}M", cand.Start.Current, cand.End.Current,
                        $"Start {Describe(reading.Start)}, End {Describe(reading.End)}", text);
                }
            }

            private static string Describe(SideReading r)
                => r.Time.HasValue ? $"[{r.Label}] {r.Time.Value:mm\\:ss\\.fff} {r.How}" : r.How;

            /// <summary>
            /// After the last run: each boundary = the pick of the runs (OnDisagreement), or the snap when no run answered; then the silence
            /// is trimmed, and the confidence is computed (per side: the share of the runs that gave the picked marker, tight; the subtitle:
            /// the lower of its two sides; a single run: 0.5 when tight, 0.3 otherwise, as nothing cross-checks it).
            /// </summary>
            private void Decide(TimingCandidate cand, CandidateState state)
            {
                state.Decided = true;
                var prevStart = cand.Start.Current;
                var prevEnd = cand.End.Current;
                var runs = state.Readings.Count;
                var scores = new Dictionary<BoundarySide, double>();
                var reasons = new List<string>();
                var missing = new HashSet<BoundarySide>();

                // A run that heard the whole line outside of its place (its Start at or after the snapped End, and its End after every marker;
                // or the mirror) found the words somewhere else (ex. the same words in the next line): it says nothing about this line, ignored
                // (it still counts in 'runs': the confidence is lower)
                var usable = new List<Reading>();
                for (int k = 0; k < state.Readings.Count; k++)
                {
                    var reading = state.Readings[k];
                    bool outside = (reading.End.Beyond && reading.Start.Time.HasValue && reading.Start.Time.Value >= state.SnappedEnd)
                        || (reading.Start.Beyond && reading.End.Time.HasValue && reading.End.Time.Value <= state.SnappedStart);
                    if (outside)
                        reasons.Add($"run {k + 1}: the whole line {(reading.End.Beyond ? "after" : "before")} its place (ignored)");
                    else
                        usable.Add(reading);
                }

                foreach (var side in TimingCandidate.Sides)
                {
                    // A run that heard the line start before every marker (end after every marker) votes for the outermost marker:
                    // the boundary is beyond the options (the snapped boundary is too far inside), flagged MissingStart / MissingEnd
                    var outermost = side == BoundarySide.Start ? state.Markers[0].Time : state.Markers[state.Markers.Count - 1].Time;
                    int beyond = usable.Count(r => r[side].Beyond);
                    var times = usable
                        .Select(r => r[side].Beyond ? outermost : r[side].Time)
                        .Where(t => t.HasValue).Select(t => t.Value).ToList();
                    if (times.Count == 0)
                    {
                        scores[side] = 0.0;
                        reasons.Add($"{side}: no answer (kept as snapped)");
                        this.Refiner.Flag(cand, ValidationCode.NoAnswer, $"No usable {side} in the answers of the AI, kept the snapped {side}");
                        continue;
                    }

                    var pick = r_engine.OnDisagreement == SpokenMarkersPick.Majority ? Majority(side, times) : Wider(side, times);
                    cand[side].Current = pick;
                    MarkDipSelected(cand, side, pick);

                    int agree = usable.Count(r => r[side].Time == pick && r[side].Tight);
                    if (runs == 1)
                    {
                        scores[side] = state.Readings[0][side].Tight ? 0.5 : 0.3;
                        reasons.Add($"{side}: 1 run only{(state.Readings[0][side].Tight ? "" : ", loose")}");
                    }
                    else
                    {
                        scores[side] = (double)agree / runs;
                        reasons.Add($"{side}: {agree}/{runs} runs agree");
                    }

                    if (beyond > 0)
                    {
                        missing.Add(side);
                        reasons.Add($"{side}: beyond the options in {beyond}/{runs} run(s)");
                        this.Refiner.Flag(cand, side == BoundarySide.Start ? ValidationCode.MissingStart : ValidationCode.MissingEnd,
                            $"Missing {side} (spoken markers): in {beyond}/{runs} run(s), the line {(side == BoundarySide.Start ? "starts before" : "ends after")} " +
                            $"every marker; {side} set to {(pick == outermost ? "the outermost option" : "the pick of the other runs")} ({pick:mm\\:ss\\.fff})");
                    }
                }

                if (cand.Start.Current >= cand.End.Current - TimingRefiner.MinClipDuration)
                {
                    cand.Start.Current = prevStart;
                    cand.End.Current = prevEnd;
                    scores[BoundarySide.Start] = scores[BoundarySide.End] = 0.0;
                    reasons.Add("the picked Start was after the picked End: kept as snapped");
                    this.Refiner.Flag(cand, ValidationCode.BoundariesCrossed, "Boundaries crossed (spoken markers), kept the snapped timing");
                }
                else
                {
                    // A side that is not confirmed by every run (same marker, tight) is flagged: UnsureStart / UnsureEnd (each can be
                    // ignored on its own in Validation.Validations, ex. to validate only the Starts). A side without any answer is already NoAnswer.
                    foreach (var side in TimingCandidate.Sides)
                    {
                        if (scores[side] >= 1.0 || missing.Contains(side) || !usable.Any(r => r[side].Time.HasValue))
                            continue;
                        var detail = runs == 1
                            ? "a single run (nothing to cross-check it)"
                            : $"{usable.Count(r => r[side].Time == cand[side].Current && r[side].Tight)}/{runs} runs agree: " +
                              string.Join(" | ", state.Readings.Select((r, k) => $"run {k + 1}: {Describe(r[side])}"));
                        this.Refiner.Flag(cand, side == BoundarySide.Start ? ValidationCode.UnsureStart : ValidationCode.UnsureEnd,
                            $"Unsure {side} (spoken markers): {detail}");
                    }
                }

                cand.LockAll();
                cand.Trace("Pick", prevStart, prevEnd, $"{runs} run(s), {r_engine.OnDisagreement}");
                foreach (var side in TimingCandidate.Sides)
                    this.Refiner.TrimSilence(cand, side);

                cand.TimingConfidence = Math.Round(Math.Min(scores[BoundarySide.Start], scores[BoundarySide.End]), 2);
                cand.TimingConfidenceReason = $"Spoken markers: {string.Join(", ", reasons)}";
            }

            private static TimeSpan Wider(BoundarySide side, List<TimeSpan> times)
                => side == BoundarySide.Start ? times.Min() : times.Max();

            private static TimeSpan Majority(BoundarySide side, List<TimeSpan> times)
            {
                var groups = times.GroupBy(t => t).Select(g => (Time: g.Key, Count: g.Count())).ToList();
                var best = groups.Max(g => g.Count);
                return Wider(side, groups.Where(g => g.Count == best).Select(g => g.Time).ToList());
            }

            /// <summary>Whatever was not decided (interrupted) is decided with what was answered, then each End is capped by the next Start.</summary>
            public override void End()
            {
                foreach (var kv in r_states.Where(kv => !kv.Value.Decided).ToList())
                {
                    kv.Key.CurrentRound = null;
                    Decide(kv.Key, kv.Value);
                }

                CapEndsByNextStarts();
                RevertInsideNeighbours();
                LockUnfinished(this.Refiner, BoundarySide.Start);
                LockUnfinished(this.Refiner, BoundarySide.End);
            }

            /// <summary>
            /// The picks are made subtitle by subtitle (and 'Wider' moves them outward): an End after the next Start goes back to the end of
            /// the last speech before that Start (or to that Start). Its confidence is then at most 0.5. Too short to cap: left to the
            /// final overlap check.
            /// </summary>
            private void CapEndsByNextStarts()
            {
                var candidates = this.Refiner.Candidates;
                for (int i = 0; i < candidates.Count - 1; i++)
                {
                    var cur = candidates[i];
                    var next = candidates[i + 1];
                    if (cur.End.Current <= next.Start.Current || !r_states.ContainsKey(cur))
                        continue;

                    var cap = this.Refiner.TerrainMapping.TrimSilence(next.Start.Current, BoundarySide.End);
                    if (cap > next.Start.Current)
                        cap = next.Start.Current;
                    if (cap <= cur.Start.Current + TimingRefiner.MinClipDuration)
                        continue;

                    var prevEnd = cur.End.Current;
                    cur.End.Current = cap;
                    cur.Trace("CapE", cur.Start.Current, prevEnd, $"Overruled by the next Start ({next.Start.Current:mm\\:ss\\.fff})");
                    if (cur.TimingConfidence.HasValue)
                    {
                        cur.TimingConfidence = Math.Min(cur.TimingConfidence.Value, 0.5);
                        cur.TimingConfidenceReason += " (End capped by the next Start)";
                    }
                    this.Refiner.Flag(cur, ValidationCode.UnsureEnd,
                        $"Unsure End (spoken markers): it was after the next Start ({next.Start.Current:mm\\:ss\\.fff}), moved back to {cap:mm\\:ss\\.fff}");
                }
            }

            /// <summary>
            /// Still overlapping after CapEndsByNextStarts, more than the final clamp fixes (ex. the AI heard the words of a line inside the next
            /// line: both start at the same place, too short to cap): the one with the lower confidence (same: the first one) goes back to its
            /// snapped timing, when that doesn't overlap its neighbours (otherwise the other one; neither: left to the final overlap check, flagged
            /// Overlap). Its confidence is then 0, flagged InsideNeighbour. Only moves timings: it never stops the work.
            /// </summary>
            private void RevertInsideNeighbours()
            {
                var candidates = this.Refiner.Candidates;
                for (int i = 0; i < candidates.Count - 1; i++)
                {
                    var cur = candidates[i];
                    var next = candidates[i + 1];
                    if (cur.End.Current - next.Start.Current <= TimingRefiner.MaxFinalClampOverlap)
                        continue;

                    var curConf = cur.TimingConfidence ?? 0.0;
                    var nextConf = next.TimingConfidence ?? 0.0;
                    var order = nextConf < curConf ? new[] { i + 1, i } : new[] { i, i + 1 };
                    foreach (var k in order)
                    {
                        if (TryRevertToSnapped(candidates, k, k == i ? next : cur))
                            break;
                    }
                }
            }

            private bool TryRevertToSnapped(IReadOnlyList<TimingCandidate> candidates, int k, TimingCandidate other)
            {
                var cand = candidates[k];
                if (!r_states.TryGetValue(cand, out var state))
                    return false;
                var start = state.SnappedStart;
                var end = state.SnappedEnd;
                if (end - start < TimingRefiner.MinClipDuration
                    || (k > 0 && candidates[k - 1].End.Current > start)
                    || (k + 1 < candidates.Count && candidates[k + 1].Start.Current < end))
                    return false;

                var prevStart = cand.Start.Current;
                var prevEnd = cand.End.Current;
                cand.Start.Current = start;
                cand.End.Current = end;
                cand.Trace("Rvrt", prevStart, prevEnd, $"Inside its neighbour ({other.Start.Current:mm\\:ss\\.fff}=>{other.End.Current:mm\\:ss\\.fff}): back to the snapped timing");
                cand.TimingConfidence = 0.0;
                cand.TimingConfidenceReason = (cand.TimingConfidenceReason == null ? "" : cand.TimingConfidenceReason + " ") + "(inside its neighbour: back to the snapped timing)";
                this.Refiner.Flag(cand, ValidationCode.InsideNeighbour,
                    $"Inside its neighbour (spoken markers): the timing picked ({prevStart:mm\\:ss\\.fff}=>{prevEnd:mm\\:ss\\.fff}) overlapped " +
                    $"{other.Start.Current:mm\\:ss\\.fff}=>{other.End.Current:mm\\:ss\\.fff}, back to the snapped timing (the same words as the other line? not in the audio?)");
                return true;
            }

            private static TimeSpan TimeSpanMin(TimeSpan a, TimeSpan b) => a < b ? a : b;
            private static TimeSpan TimeSpanMax(TimeSpan a, TimeSpan b) => a > b ? a : b;
        }
    }
}
