using FunscriptToolbox.SubtitlesVerbs.AudioExtractions;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using FunscriptToolbox.Core;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    public class TimingClipOptionV2
    {
        public string FieldName { get; set; }
        public string StartId { get; set; }
        public string EndId { get; set; }
        public TimeSpan Start { get; set; }
        public TimeSpan End { get; set; }
        public TimeSpan Duration => End - Start;
    }

    public class TimingRoundV2
    {
        public int RoundNumber { get; set; }
        public TimeSpan CenterStart { get; set; }
        public TimeSpan CenterEnd { get; set; }
        public Dictionary<string, TimeSpan> StartOptions { get; set; } = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, TimeSpan> EndOptions { get; set; } = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
        public List<TimingClipOptionV2> Clips { get; set; } = new List<TimingClipOptionV2>();

        public string BestStart { get; set; }
        public string BestEnd { get; set; }
        public string HeardText { get; set; }
    }

    public class TimingCandidateV2
    {
        public TimeSpan OriginalStartTime { get; set; }
        public TimeSpan OriginalEndTime { get; set; }
        public string OriginalText { get; set; }

        [JsonIgnore] public TimedItemWithMetadata OriginalItem { get; set; }

        // Initial snapped boundaries
        public TimeSpan InitStart { get; set; }
        public TimeSpan InitEnd { get; set; }

        // Current best / working boundaries
        public TimeSpan CurrentTestStart { get; set; }
        public TimeSpan CurrentTestEnd { get; set; }

        public TimeSpan? HardLimitStart { get; set; }
        public TimeSpan? HardLimitEnd { get; set; }

        public bool StartLocked { get; set; }
        public bool EndLocked { get; set; }
        [JsonIgnore] public bool IsFullyLocked => StartLocked && EndLocked;

        public int RetryCount { get; set; }
        public bool WasResetByOverlap { get; set; }
        public string PossibleBleedFromPrev { get; set; }
        public string PossibleBleedFromNext { get; set; }

        public TimingRoundV2 CurrentRound { get; set; }
        public List<TimingRoundV2> Rounds { get; set; } = new List<TimingRoundV2>();

        public List<string> ValidationNotes { get; set; } = new List<string>();
        public List<string> Traces { get; set; } = new List<string>();

        public void LockAll()
        {
            StartLocked = true;
            EndLocked = true;
        }

        public void AddValidationNote(string note)
        {
            if (!ValidationNotes.Contains(note))
                ValidationNotes.Add(note);
        }

        public string GetLockTag()
        {
            if (StartLocked && EndLocked) return "**";
            if (StartLocked) return "S?";
            if (EndLocked) return "?E";
            return "??";
        }

        public void AddTraceRow(string step, char startMod, TimeSpan start, TimeSpan end, char endMod, string result = null, string text = null)
        {
            string lockTag = GetLockTag();
            string stepCol = (step ?? "").PadRight(4);
            string boundsCol = $"[{startMod}{start:hh\\:mm\\:ss\\.fff}=>{end:hh\\:mm\\:ss\\.fff}{endMod}]";

            if (string.IsNullOrEmpty(result) && string.IsNullOrEmpty(text))
            {
                Traces.Add($"{lockTag} {stepCol} {boundsCol}");
            }
            else
            {
                string resCol = (result ?? "").PadRight(24);
                string textCol = text != null ? $"'{text}'" : "";
                Traces.Add($"{lockTag} {stepCol} {boundsCol} {resCol} {textCol}".TrimEnd());
            }
        }

        public Subtitle ToSubtitle()
        {
            return new Subtitle(this.CurrentTestStart, this.CurrentTestEnd, new[] { this.OriginalText });
        }
    }

    /// <summary>
    /// Refines the timing of transcription candidates by generating multi-clip comparative options
    /// (Max, M{N}..M1, Init, P1..P{N}) around terrain dips and asking the AI to judge the best Start and End boundaries.
    /// </summary>
    public class TimingRefinerV2
    {
        public int MaxRetriesPerNode { get; set; } = 3;
        public int MaxMountainSteps { get; set; } = 2;
        public TimeSpan MaxExpansion { get; set; } = TimeSpan.FromSeconds(2.0);
        public TimeSpan BleedDetectionMargin { get; set; } = TimeSpan.FromSeconds(3.0);
        public TimeSpan OutwardNudgeDuration { get; set; } = TimeSpan.FromMilliseconds(30);
        public TimeSpan MinSubtitleDuration { get; set; } = TimeSpan.FromSeconds(0.5);

        private double MinJumpSeconds { get; set; } = 0.07;

        public AudioTerrainMapping TerrainMapping { get; set; }
        public TimeSpan AudioEndTime { get; private set; }

        public List<TimingCandidateV2> Candidates { get; set; } = new List<TimingCandidateV2>();

        public TimingRefinerV2(
            PcmAudio fullPcmAudioForRefiner,
            int maxRetriesPerNode,
            int maxMountainSteps,
            TimeSpan maxExpansion,
            TimeSpan bleedDetectionMargin,
            TimeSpan outwardNudgeDuration,
            TimeSpan minSubtitleDuration,
            float valleyReach)
        {
            this.MaxRetriesPerNode = maxRetriesPerNode;
            this.MaxMountainSteps = Math.Max(0, maxMountainSteps);
            this.MaxExpansion = maxExpansion;
            this.BleedDetectionMargin = bleedDetectionMargin;
            this.OutwardNudgeDuration = outwardNudgeDuration;
            this.MinSubtitleDuration = minSubtitleDuration;
            this.AudioEndTime = fullPcmAudioForRefiner?.EndTime ?? TimeSpan.MaxValue;

            TerrainMapping = new AudioTerrainMapping(fullPcmAudioForRefiner)
            {
                SpeechRatio = 4f,
                PeakRatio = 6f,
                ValleyReach = valleyReach,
            };
            TerrainMapping.Compute();
        }

        public void AddCandidate(TimedItemWithMetadata originalItem, string originalText)
        {
            var candidate = new TimingCandidateV2
            {
                OriginalStartTime = originalItem.StartTime,
                OriginalEndTime = originalItem.EndTime,
                OriginalItem = originalItem,
                OriginalText = originalText,
                CurrentTestStart = originalItem.StartTime,
                CurrentTestEnd = originalItem.EndTime,
            };

            TimeSpan start = SnapInitialBoundary(candidate, BoundarySide.Start);
            TimeSpan end = SnapInitialBoundary(candidate, BoundarySide.End);

            if (end <= start)
            {
                start = originalItem.StartTime;
                end = originalItem.EndTime;
            }

            EnforceMinimumDuration(candidate, ref start, ref end);

            candidate.InitStart = start;
            candidate.InitEnd = end;
            candidate.CurrentTestStart = start;
            candidate.CurrentTestEnd = end;

            candidate.AddTraceRow("Orig", ' ', originalItem.StartTime, originalItem.EndTime, ' ', "", candidate.OriginalText);
            char initStartMod = TimingCandidate.GetStartMod(originalItem.StartTime, start);
            char initEndMod = TimingCandidate.GetEndMod(originalItem.EndTime, end);
            candidate.AddTraceRow("Init", initStartMod, start, end, initEndMod, "Snapped initial bounds");

            this.Candidates.Add(candidate);
        }

        public TimeSpan SnapInitialBoundary(TimingCandidateV2 cand, BoundarySide side)
        {
            if (TerrainMapping == null)
                return side == BoundarySide.Start ? cand.OriginalStartTime : cand.OriginalEndTime;

            bool isStart = side == BoundarySide.Start;
            var t = isStart ? cand.OriginalStartTime : cand.OriginalEndTime;
            var maxSnapDrift = TimeSpan.FromSeconds(1.2);

            // 1. Silent: snap inward towards speech edge
            if (TerrainMapping.IsSilent(t))
            {
                var inwardLimit = isStart ? t + maxSnapDrift : t - maxSnapDrift;

                var edge = TerrainMapping.FindDip(t, side, MoveDirection.Inward, new DipSearch
                {
                    MinJump = TimeSpan.MaxValue,
                    StopAtSpeechEdge = true,
                    Inclusive = true,
                    Limit = inwardLimit
                });
                if (edge.HasValue && (edge.Value - t).Duration() <= maxSnapDrift)
                    return edge.Value;

                var dip = TerrainMapping.FindDip(t, side, MoveDirection.Inward, new DipSearch { Inclusive = true, Limit = inwardLimit });
                if (dip.HasValue && (dip.Value - t).Duration() <= maxSnapDrift)
                    return dip.Value;
                return t;
            }

            // 2. Inside a mountain: snap outward to speech edge
            var islandEdge = TerrainMapping.FindDip(t, side, MoveDirection.Outward, new DipSearch
            {
                StopAtSpeechEdge = true,
                Inclusive = true,
                Limit = isStart ? t - maxSnapDrift : t + maxSnapDrift
            });

            if (islandEdge.HasValue && (islandEdge.Value - t).Duration() <= maxSnapDrift)
                return islandEdge.Value;

            var snapped = TerrainMapping.FindDip(t, side, MoveDirection.Outward, new DipSearch { Inclusive = true });
            return snapped.HasValue && (snapped.Value - t).Duration() <= maxSnapDrift
                ? snapped.Value
                : t;
        }

        public void EnforceMinimumDuration(TimingCandidateV2 cand, ref TimeSpan start, ref TimeSpan end)
        {
            var minViableDuration = TimeSpan.FromSeconds(0.05);
            var maxSnapDrift = TimeSpan.FromSeconds(1.2);

            var prev = this.Candidates.LastOrDefault();
            if (prev != null && start <= prev.CurrentTestStart)
            {
                var nextDip = TerrainMapping?.FindDip(
                    prev.CurrentTestStart,
                    BoundarySide.Start,
                    MoveDirection.Inward,
                    new DipSearch
                    {
                        Inclusive = false,
                        Limit = cand.OriginalStartTime + maxSnapDrift
                    });

                if (nextDip.HasValue && (nextDip.Value - cand.OriginalStartTime).Duration() <= maxSnapDrift)
                {
                    start = nextDip.Value;
                }
                else if (cand.OriginalStartTime > prev.CurrentTestStart)
                {
                    start = cand.OriginalStartTime;
                }

                if (end <= start)
                {
                    end = cand.OriginalEndTime > start ? cand.OriginalEndTime : start + minViableDuration;
                }
            }

            if (end <= start)
            {
                end = cand.OriginalEndTime > start ? cand.OriginalEndTime : start + minViableDuration;
            }
        }

        /// <summary>
        /// Prepares the next round of clip options (Audio_Max_Max first, then M2, M1, Init, P1, P2)
        /// and neighbour bleed metadata for all unlocked candidates.
        /// </summary>
        public void PrepareNextRound()
        {
            var sorted = this.Candidates.OrderBy(c => c.OriginalStartTime).ToList();

            for (int i = 0; i < sorted.Count; i++)
            {
                var cand = sorted[i];
                if (cand.IsFullyLocked || cand.RetryCount >= this.MaxRetriesPerNode)
                {
                    cand.CurrentRound = null;
                    continue;
                }

                // Determine PossibleBleedFromPrev and PossibleBleedFromNext
                cand.PossibleBleedFromPrev = null;
                cand.PossibleBleedFromNext = null;

                if (i > 0)
                {
                    var prev = sorted[i - 1];
                    var gapBefore = cand.CurrentTestStart - prev.CurrentTestEnd;
                    if (gapBefore <= this.BleedDetectionMargin && !string.IsNullOrWhiteSpace(prev.OriginalText))
                    {
                        cand.PossibleBleedFromPrev = prev.OriginalText;
                    }
                }

                if (i < sorted.Count - 1)
                {
                    var next = sorted[i + 1];
                    var gapAfter = next.CurrentTestStart - cand.CurrentTestEnd;
                    if (gapAfter <= this.BleedDetectionMargin && !string.IsNullOrWhiteSpace(next.OriginalText))
                    {
                        cand.PossibleBleedFromNext = next.OriginalText;
                    }
                }

                cand.CurrentRound = BuildRoundForCandidate(cand);
            }
        }

        private string[] GetStepLevels()
        {
            var list = new List<string>(this.MaxMountainSteps * 2 + 1);
            for (int k = this.MaxMountainSteps; k >= 1; k--)
                list.Add($"M{k}");
            list.Add("Init");
            for (int k = 1; k <= this.MaxMountainSteps; k++)
                list.Add($"P{k}");
            return list.ToArray();
        }

        private TimingRoundV2 BuildRoundForCandidate(TimingCandidateV2 cand)
        {
            var centerStart = cand.CurrentTestStart;
            var centerEnd = cand.CurrentTestEnd;

            var round = new TimingRoundV2
            {
                RoundNumber = cand.RetryCount + 1,
                CenterStart = centerStart,
                CenterEnd = centerEnd
            };

            string[] levels = GetStepLevels();
            int initIndex = this.MaxMountainSteps;

            var rawStartSteps = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
            var rawEndSteps = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);

            // Compute Start boundaries
            var startInit = centerStart;
            var startMax = ClampToAudioBounds(startInit - this.MaxExpansion);
            if (cand.HardLimitStart.HasValue && startMax < cand.HardLimitStart.Value)
                startMax = cand.HardLimitStart.Value;

            rawStartSteps["Init"] = startInit;

            TimeSpan prevStartInward = startInit;
            var startInwardLimit = centerEnd - TimeSpan.FromSeconds(0.05);
            for (int k = 1; k <= this.MaxMountainSteps; k++)
            {
                TimeSpan nextStartInward = prevStartInward;
                if (!cand.StartLocked && TerrainMapping != null && prevStartInward < startInwardLimit)
                {
                    var dip = TerrainMapping.FindDip(prevStartInward, BoundarySide.Start, MoveDirection.Inward, new DipSearch
                    {
                        MinJump = TimeSpan.FromSeconds(MinJumpSeconds),
                        StopAtSpeechEdge = true,
                        Limit = startInwardLimit
                    });
                    if (dip.HasValue && dip.Value < startInwardLimit)
                        nextStartInward = dip.Value;
                }
                rawStartSteps[$"M{k}"] = nextStartInward;
                prevStartInward = nextStartInward;
            }

            TimeSpan prevStartOutward = startInit;
            for (int k = 1; k <= this.MaxMountainSteps; k++)
            {
                TimeSpan nextStartOutward = prevStartOutward;
                if (!cand.StartLocked && TerrainMapping != null && prevStartOutward > startMax)
                {
                    var dip = TerrainMapping.FindDip(prevStartOutward, BoundarySide.Start, MoveDirection.Outward, new DipSearch
                    {
                        MinJump = TimeSpan.FromSeconds(MinJumpSeconds),
                        StopAtSpeechEdge = true,
                        Limit = startMax
                    });
                    if (dip.HasValue && dip.Value >= startMax)
                        nextStartOutward = dip.Value;
                }
                rawStartSteps[$"P{k}"] = nextStartOutward;
                prevStartOutward = nextStartOutward;
            }

            // Compute End boundaries
            var endInit = centerEnd;
            var endMax = ClampToAudioBounds(endInit + this.MaxExpansion);
            if (cand.HardLimitEnd.HasValue && endMax > cand.HardLimitEnd.Value)
                endMax = cand.HardLimitEnd.Value;
            if (endMax <= startInit)
                endMax = ClampToAudioBounds(startInit + TimeSpan.FromSeconds(0.05));

            rawEndSteps["Init"] = endInit;

            TimeSpan prevEndInward = endInit;
            var endInwardLimit = centerStart + TimeSpan.FromSeconds(0.05);
            for (int k = 1; k <= this.MaxMountainSteps; k++)
            {
                TimeSpan nextEndInward = prevEndInward;
                if (!cand.EndLocked && TerrainMapping != null && prevEndInward > endInwardLimit)
                {
                    var dip = TerrainMapping.FindDip(prevEndInward, BoundarySide.End, MoveDirection.Inward, new DipSearch
                    {
                        MinJump = TimeSpan.FromSeconds(MinJumpSeconds),
                        StopAtSpeechEdge = true,
                        Limit = endInwardLimit
                    });
                    if (dip.HasValue && dip.Value > endInwardLimit)
                        nextEndInward = dip.Value;
                }
                rawEndSteps[$"M{k}"] = nextEndInward;
                prevEndInward = nextEndInward;
            }

            TimeSpan prevEndOutward = endInit;
            for (int k = 1; k <= this.MaxMountainSteps; k++)
            {
                TimeSpan nextEndOutward = prevEndOutward;
                if (!cand.EndLocked && TerrainMapping != null && prevEndOutward < endMax)
                {
                    var dip = TerrainMapping.FindDip(prevEndOutward, BoundarySide.End, MoveDirection.Outward, new DipSearch
                    {
                        MinJump = TimeSpan.FromSeconds(MinJumpSeconds),
                        StopAtSpeechEdge = true,
                        Limit = endMax
                    });
                    if (dip.HasValue && dip.Value <= endMax)
                        nextEndOutward = dip.Value;
                }
                rawEndSteps[$"P{k}"] = nextEndOutward;
                prevEndOutward = nextEndOutward;
            }

            // Build candidate windows from tightest (M{N}) to widest (P{N})
            var candidateWindows = new List<(TimeSpan Start, TimeSpan End)>();
            var seenCandidateWindows = new HashSet<(TimeSpan, TimeSpan)>();
            var minClipDuration = TimeSpan.FromSeconds(0.05);

            for (int lvl = 0; lvl < levels.Length; lvl++)
            {
                string levelName = levels[lvl];
                var s = rawStartSteps[levelName];
                var e = rawEndSteps[levelName];

                // If shrinking inward on both sides crosses or makes the clip zero, try backing off one side toward Init
                if (lvl < initIndex && e - s < minClipDuration)
                {
                    int stepTowardInit = lvl + 1;
                    while (stepTowardInit <= initIndex && e - s < minClipDuration)
                    {
                        var altS = rawStartSteps[levels[stepTowardInit]];
                        var altE = rawEndSteps[levels[stepTowardInit]];

                        if (e - altS >= minClipDuration)
                        {
                            s = altS;
                            break;
                        }
                        if (altE - s >= minClipDuration)
                        {
                            e = altE;
                            break;
                        }
                        s = altS;
                        e = altE;
                        stepTowardInit++;
                    }
                }

                if (e - s >= minClipDuration && seenCandidateWindows.Add((s, e)))
                {
                    candidateWindows.Add((s, e));
                }
            }

            if (candidateWindows.Count == 0)
            {
                var fallbackEnd = endInit > startInit + minClipDuration ? endInit : ClampToAudioBounds(startInit + minClipDuration);
                candidateWindows.Add((startInit, fallbackEnd));
            }

            // Assign neutral labels C1..C{K} ordered from tightest (innermost) to widest (outermost)
            var distinctStarts = candidateWindows
                .Select(w => w.Start)
                .Distinct()
                .OrderByDescending(t => t) // latest Start = tightest (C1), earliest Start = widest (C{K})
                .ToList();

            var distinctEnds = candidateWindows
                .Select(w => w.End)
                .Distinct()
                .OrderBy(t => t) // earliest End = tightest (C1), latest End = widest (C{K})
                .ToList();

            var startTimeToId = new Dictionary<TimeSpan, string>();
            for (int idx = 0; idx < distinctStarts.Count; idx++)
            {
                string id = $"C{idx + 1}";
                startTimeToId[distinctStarts[idx]] = id;
                round.StartOptions[id] = distinctStarts[idx];
            }

            var endTimeToId = new Dictionary<TimeSpan, string>();
            for (int idx = 0; idx < distinctEnds.Count; idx++)
            {
                string id = $"C{idx + 1}";
                endTimeToId[distinctEnds[idx]] = id;
                round.EndOptions[id] = distinctEnds[idx];
            }

            // If the stretch between the widest candidate option and Max is 100% silence, clamp Max to the widest candidate option
            var widestStartOption = distinctStarts[distinctStarts.Count - 1];
            if (TerrainMapping != null && startMax < widestStartOption && !TerrainMapping.HasSpeechBetween(startMax, widestStartOption))
            {
                startMax = widestStartOption;
            }

            var widestEndOption = distinctEnds[distinctEnds.Count - 1];
            if (TerrainMapping != null && endMax > widestEndOption && !TerrainMapping.HasSpeechBetween(widestEndOption, endMax))
            {
                endMax = widestEndOption;
            }

            round.StartOptions["Max"] = startMax;
            round.EndOptions["Max"] = endMax;

            // 1. Add Audio_Max_Max FIRST so the AI listens to the wide reference window first
            if (endMax - startMax >= minClipDuration)
            {
                round.Clips.Add(new TimingClipOptionV2
                {
                    FieldName = "Audio_Max_Max",
                    StartId = "Max",
                    EndId = "Max",
                    Start = startMax,
                    End = endMax
                });
            }

            // 2. Add Audio_C1_C1 .. Audio_C{K}_C{K} from tightest to widest
            foreach (var (s, e) in candidateWindows)
            {
                string sLabel = startTimeToId[s];
                string eLabel = endTimeToId[e];
                string fieldName = $"Audio_{sLabel}_{eLabel}";

                if (!round.Clips.Any(c => string.Equals(c.FieldName, fieldName, StringComparison.OrdinalIgnoreCase)))
                {
                    round.Clips.Add(new TimingClipOptionV2
                    {
                        FieldName = fieldName,
                        StartId = sLabel,
                        EndId = eLabel,
                        Start = s,
                        End = e
                    });
                }
            }

            // Trace Start and End boundary options ordered from outside (Max, C{K}..C1) to inside, marking the Center/Init boundary with '*'
            string FormatOptionsOutsideToInside(Dictionary<string, TimeSpan> opts, int count, TimeSpan centerTime)
            {
                var parts = new List<string>();
                if (opts.TryGetValue("Max", out var maxT))
                {
                    parts.Add($"Max={maxT:mm\\:ss\\.fff}");
                }
                for (int idx = count; idx >= 1; idx--)
                {
                    string key = $"C{idx}";
                    if (opts.TryGetValue(key, out var t))
                    {
                        string marker = (t == centerTime) ? "*" : "";
                        parts.Add($"{key}{marker}={t:mm\\:ss\\.fff}");
                    }
                }
                return string.Join(", ", parts);
            }

            string startOptsSummary = FormatOptionsOutsideToInside(round.StartOptions, distinctStarts.Count, centerStart);
            string endOptsSummary = FormatOptionsOutsideToInside(round.EndOptions, distinctEnds.Count, centerEnd);

            cand.Traces.Add($"#{round.RoundNumber} Options:");
            cand.Traces.Add($"     [Start] {startOptsSummary}");
            cand.Traces.Add($"     [End]   {endOptsSummary}");

            return round;
        }

        public void ProcessBatchResults(IEnumerable<TranscribedItem> responseItems)
        {
            foreach (var item in responseItems)
            {
                var cand = this.Candidates.FirstOrDefault(c => c.CurrentRound != null && c.OriginalStartTime == item.StartTime);
                if (cand == null)
                {
                    cand = this.Candidates.FirstOrDefault(c => c.CurrentRound != null && Math.Abs((c.OriginalStartTime - item.StartTime).TotalMilliseconds) < 50);
                }
                if (cand == null)
                    continue;

                var round = cand.CurrentRound;
                cand.RetryCount++;

                item.Metadata.TryGetValue("BestStart", out var bestStartRaw);
                item.Metadata.TryGetValue("BestEnd", out var bestEndRaw);
                item.Metadata.TryGetValue("HeardText", out var heardTextRaw);

                round.BestStart = bestStartRaw?.Trim();
                round.BestEnd = bestEndRaw?.Trim();
                round.HeardText = heardTextRaw?.Trim();
                cand.Rounds.Add(round);
                cand.CurrentRound = null;

                var prevStart = cand.CurrentTestStart;
                var prevEnd = cand.CurrentTestEnd;

                // Check for Inaudible
                if (IsInaudibleToken(round.BestStart) || IsInaudibleToken(round.BestEnd))
                {
                    cand.LockAll();
                    cand.AddValidationNote($"Unsure: Audio Inaudible (original): {cand.OriginalText}");
                    cand.AddTraceRow($"#{cand.RetryCount}", ' ', cand.CurrentTestStart, cand.CurrentTestEnd, ' ', $"AI: Inaudible", round.HeardText);
                    continue;
                }

                // Resolve Start
                if (!cand.StartLocked)
                {
                    var (newStart, lockedStart, needsMoreStart) = ResolveBoundaryChoice(cand, round, BoundarySide.Start, round.BestStart);
                    cand.CurrentTestStart = newStart;
                    cand.StartLocked = lockedStart;
                    if (needsMoreStart && (lockedStart || cand.RetryCount >= this.MaxRetriesPerNode))
                    {
                        cand.StartLocked = true;
                        cand.AddValidationNote("Unsure: Missing Start (Max expansion reached)");
                    }
                }

                // Resolve End
                if (!cand.EndLocked)
                {
                    var (newEnd, lockedEnd, needsMoreEnd) = ResolveBoundaryChoice(cand, round, BoundarySide.End, round.BestEnd);
                    cand.CurrentTestEnd = newEnd;
                    cand.EndLocked = lockedEnd;
                    if (needsMoreEnd && (lockedEnd || cand.RetryCount >= this.MaxRetriesPerNode))
                    {
                        cand.EndLocked = true;
                        cand.AddValidationNote("Unsure: Missing End (Max expansion reached)");
                    }
                }

                // Guard against crossed boundaries
                if (cand.CurrentTestEnd <= cand.CurrentTestStart)
                {
                    cand.CurrentTestStart = round.CenterStart;
                    cand.CurrentTestEnd = round.CenterEnd;
                    cand.LockAll();
                    cand.AddValidationNote("Unsure: Boundaries crossed, kept previous timing");
                }

                // Record HeardText note if AI reported a different transcription in Max
                if (!string.IsNullOrWhiteSpace(round.HeardText) &&
                    !string.Equals(round.HeardText, "null", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(round.HeardText, cand.OriginalText, StringComparison.OrdinalIgnoreCase))
                {
                    cand.AddValidationNote($"Unsure: Text Mismatch (original vs heard in Max): `{cand.OriginalText}` vs `{round.HeardText}`");
                }

                char sMod = TimingCandidate.GetStartMod(prevStart, cand.CurrentTestStart);
                char eMod = TimingCandidate.GetEndMod(prevEnd, cand.CurrentTestEnd);
                string choiceSummary = $"S={round.BestStart ?? "?"} E={round.BestEnd ?? "?"}";
                cand.AddTraceRow($"#{cand.RetryCount}", sMod, cand.CurrentTestStart, cand.CurrentTestEnd, eMod, choiceSummary, round.HeardText);
            }

            CoordinateNeighborOverlaps();
        }

        /// <summary>
        /// Resolves overlaps between adjacent candidates at the end of an iteration:
        /// - Tiny overlaps (&lt;= OutwardNudgeDuration * 2) are clamped cleanly.
        /// - Case A (Normal overlap where cur has a valid mountain End dip at or before next.Start):
        ///   next.Start overrules cur.End (cur.HardLimitEnd = next.Start) and cur.End is pulled back / retried.
        /// - Case B (Stolen-mountain / total overlap where next.Start jumped back onto or past cur's mountain,
        ///   leaving cur no valid End dip before next.Start):
        ///   cur is locked at its current bounds, next.HardLimitStart is set to cur.End, and next is reset and rerun.
        /// </summary>
        private void CoordinateNeighborOverlaps()
        {
            var sorted = this.Candidates.OrderBy(c => c.OriginalStartTime).ToList();
            var minViableDuration = TimeSpan.FromSeconds(0.1);
            var tinyOverlapThreshold = TimeSpan.FromMilliseconds(Math.Max(60, this.OutwardNudgeDuration.TotalMilliseconds * 2));

            for (int i = 0; i < sorted.Count - 1; i++)
            {
                var cur = sorted[i];
                var next = sorted[i + 1];

                // Propagate hard limits from non-overlapping locked boundaries
                if (cur.CurrentTestEnd <= next.CurrentTestStart)
                {
                    if (next.StartLocked)
                    {
                        cur.HardLimitEnd = next.CurrentTestStart;
                    }
                    if (cur.EndLocked)
                    {
                        next.HardLimitStart = cur.CurrentTestEnd;
                    }
                    continue;
                }

                var overlap = cur.CurrentTestEnd - next.CurrentTestStart;

                // 1. Tiny overlap (e.g. from a shared valley): clamp cleanly at midpoint or next.Start
                if (overlap <= tinyOverlapThreshold && next.CurrentTestStart > cur.CurrentTestStart + minViableDuration)
                {
                    var clampPoint = next.StartLocked
                        ? next.CurrentTestStart
                        : TimeSpan.FromMilliseconds((cur.CurrentTestEnd.TotalMilliseconds + next.CurrentTestStart.TotalMilliseconds) / 2.0);

                    if (clampPoint - cur.CurrentTestStart >= minViableDuration && next.CurrentTestEnd - clampPoint >= minViableDuration)
                    {
                        cur.CurrentTestEnd = clampPoint;
                        if (!next.StartLocked)
                            next.CurrentTestStart = clampPoint;
                        cur.AddTraceRow("Clmp", ' ', cur.CurrentTestStart, cur.CurrentTestEnd, '-', $"Clamped tiny overlap with next ({clampPoint:mm\\:ss\\.fff})");
                        continue;
                    }
                }

                // 2. Check if cur has a valid mountain End dip at or before next.Start
                var hardCap = next.CurrentTestStart;
                if (TryFindBestEndAtOrBefore(cur, hardCap, minViableDuration, out var fallbackEnd))
                {
                    // Case A: Normal overlap — Start of next node overrules End of current node
                    bool alreadyCappedHere = cur.EndLocked && cur.CurrentTestEnd == fallbackEnd && cur.HardLimitEnd == hardCap;
                    cur.HardLimitEnd = hardCap;

                    var prevEnd = cur.CurrentTestEnd;
                    cur.CurrentTestEnd = fallbackEnd;
                    char endMod = TimingCandidate.GetEndMod(prevEnd, cur.CurrentTestEnd);

                    var lastRound = cur.Rounds.LastOrDefault();
                    int distinctValidEndOptions = lastRound?.EndOptions
                        .Where(kvp => !string.Equals(kvp.Key, "Max", StringComparison.OrdinalIgnoreCase) &&
                                      kvp.Value <= hardCap &&
                                      kvp.Value > cur.CurrentTestStart + minViableDuration)
                        .Select(kvp => kvp.Value)
                        .Distinct()
                        .Count() ?? 0;

                    if (cur.RetryCount < this.MaxRetriesPerNode && distinctValidEndOptions > 1)
                    {
                        cur.EndLocked = false;
                        cur.AddTraceRow("CapE", ' ', cur.CurrentTestStart, cur.CurrentTestEnd, endMod, $"Overruled by next Start ({hardCap:mm\\:ss\\.fff}) -> retry End");
                    }
                    else
                    {
                        cur.EndLocked = true;
                        if (!alreadyCappedHere)
                        {
                            cur.AddTraceRow("CapE", ' ', cur.CurrentTestStart, cur.CurrentTestEnd, endMod, $"Overruled by next Start ({hardCap:mm\\:ss\\.fff})");
                        }
                    }
                }
                else
                {
                    // Case B: Stolen-mountain / total overlap — next.Start jumped back onto or past cur's mountain, leaving cur no valid End dip.
                    // Fix cur (first node), set HardLimitStart on next (second node), reset next's bounds, and rerun next!
                    cur.StartLocked = true;
                    cur.EndLocked = true;

                    var minNextStart = cur.CurrentTestEnd;
                    bool alreadyResetHere = next.HardLimitStart == minNextStart && next.CurrentTestStart >= minNextStart;
                    next.HardLimitStart = minNextStart;
                    next.HardLimitEnd = null;

                    var prevNextStart = next.CurrentTestStart;
                    var prevNextEnd = next.CurrentTestEnd;

                    TimeSpan resetStart = next.InitStart >= minNextStart
                        ? next.InitStart
                        : FindFirstStartAtOrAfter(minNextStart);

                    TimeSpan resetEnd = next.InitEnd;
                    if (resetEnd <= resetStart + minViableDuration)
                    {
                        resetEnd = next.CurrentTestEnd > resetStart + minViableDuration
                            ? next.CurrentTestEnd
                            : FindFirstEndAfter(resetStart, this.MinSubtitleDuration);
                    }

                    next.CurrentTestStart = resetStart;
                    next.CurrentTestEnd = resetEnd;

                    if (!next.WasResetByOverlap && next.RetryCount >= this.MaxRetriesPerNode)
                    {
                        next.RetryCount = Math.Max(0, this.MaxRetriesPerNode - 1);
                    }
                    next.WasResetByOverlap = true;

                    if (next.RetryCount < this.MaxRetriesPerNode)
                    {
                        next.StartLocked = false;
                        next.EndLocked = false;
                        char sMod = TimingCandidate.GetStartMod(prevNextStart, next.CurrentTestStart);
                        char eMod = TimingCandidate.GetEndMod(prevNextEnd, next.CurrentTestEnd);
                        next.AddTraceRow("Rset", sMod, next.CurrentTestStart, next.CurrentTestEnd, eMod, $"Overlapped prev End ({minNextStart:mm\\:ss\\.fff}) -> reset & retry");
                    }
                    else
                    {
                        next.StartLocked = true;
                        next.EndLocked = true;
                        if (!alreadyResetHere)
                        {
                            char sMod = TimingCandidate.GetStartMod(prevNextStart, next.CurrentTestStart);
                            char eMod = TimingCandidate.GetEndMod(prevNextEnd, next.CurrentTestEnd);
                            next.AddTraceRow("Rset", sMod, next.CurrentTestStart, next.CurrentTestEnd, eMod, $"Overlapped prev End ({minNextStart:mm\\:ss\\.fff}) -> reset");
                        }
                    }
                }
            }
        }

        private bool TryFindBestEndAtOrBefore(TimingCandidateV2 cand, TimeSpan hardCap, TimeSpan minViableDuration, out TimeSpan bestEnd)
        {
            bestEnd = cand.CurrentTestEnd;
            var minEnd = cand.CurrentTestStart + minViableDuration;
            if (hardCap <= minEnd)
                return false;

            var lastRound = cand.Rounds.LastOrDefault();
            if (lastRound != null && lastRound.EndOptions.Count > 0)
            {
                var validFromOptions = lastRound.EndOptions
                    .Where(kvp => !string.Equals(kvp.Key, "Max", StringComparison.OrdinalIgnoreCase) && kvp.Value <= hardCap && kvp.Value > minEnd)
                    .Select(kvp => kvp.Value)
                    .OrderByDescending(t => t)
                    .ToList();

                if (validFromOptions.Count > 0)
                {
                    bestEnd = validFromOptions[0];
                    return true;
                }
            }

            if (TerrainMapping != null)
            {
                var dip = TerrainMapping.FindDip(hardCap, BoundarySide.End, MoveDirection.Inward, new DipSearch
                {
                    Inclusive = true,
                    StopAtSpeechEdge = true,
                    Limit = minEnd
                });
                if (dip.HasValue && dip.Value <= hardCap && dip.Value > minEnd)
                {
                    bestEnd = dip.Value;
                    return true;
                }
            }

            if (cand.InitEnd <= hardCap && cand.InitEnd > minEnd)
            {
                bestEnd = cand.InitEnd;
                return true;
            }

            return false;
        }

        private TimeSpan FindFirstStartAtOrAfter(TimeSpan minStart)
        {
            if (TerrainMapping != null)
            {
                var dip = TerrainMapping.FindDip(minStart, BoundarySide.Start, MoveDirection.Inward, new DipSearch
                {
                    Inclusive = true,
                    StopAtSpeechEdge = true
                });
                if (dip.HasValue && dip.Value >= minStart)
                    return dip.Value;
            }
            return minStart;
        }

        private TimeSpan FindFirstEndAfter(TimeSpan start, TimeSpan minDuration)
        {
            var target = ClampToAudioBounds(start + minDuration);
            if (TerrainMapping != null)
            {
                var dip = TerrainMapping.FindDip(target, BoundarySide.End, MoveDirection.Outward, new DipSearch
                {
                    Inclusive = true,
                    StopAtSpeechEdge = true
                });
                if (dip.HasValue && dip.Value > start)
                    return dip.Value;
            }
            return target;
        }

        private static bool IsInaudibleToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return false;
            return token.IndexOf("Inaudible", StringComparison.OrdinalIgnoreCase) >= 0
                || token.IndexOf("None", StringComparison.OrdinalIgnoreCase) >= 0
                || token.IndexOf("Silence", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static TimeSpan GetWidestCandidateTime(Dictionary<string, TimeSpan> options, bool isStart, TimeSpan fallback)
        {
            var candidateValues = options
                .Where(kvp => !string.Equals(kvp.Key, "Max", StringComparison.OrdinalIgnoreCase))
                .Select(kvp => kvp.Value)
                .ToList();

            if (candidateValues.Count == 0)
                return fallback;

            return isStart ? candidateValues.Min() : candidateValues.Max();
        }

        private (TimeSpan resolvedTime, bool isLocked, bool needsMoreExpansion) ResolveBoundaryChoice(
            TimingCandidateV2 cand,
            TimingRoundV2 round,
            BoundarySide side,
            string rawChoice)
        {
            bool isStart = side == BoundarySide.Start;
            var options = isStart ? round.StartOptions : round.EndOptions;
            var fallback = isStart ? round.CenterStart : round.CenterEnd;

            if (string.IsNullOrWhiteSpace(rawChoice))
                return (fallback, true, false);

            string token = rawChoice.Trim();

            // Strip optional Audio_ prefix if AI wrote Audio_C1_C1 or similar
            if (token.StartsWith("Audio_", StringComparison.OrdinalIgnoreCase))
            {
                var parts = token.Split('_');
                if (parts.Length >= 3)
                {
                    token = isStart ? parts[1] : parts[2];
                }
            }

            // Strip any legacy +30ms or * suffix if present
            if (token.EndsWith("+30ms", StringComparison.OrdinalIgnoreCase))
            {
                token = token.Substring(0, token.Length - 5).Trim();
            }
            else if (token.EndsWith("+30", StringComparison.OrdinalIgnoreCase))
            {
                token = token.Substring(0, token.Length - 3).Trim();
            }
            token = token.TrimEnd('*').Trim();

            // Check if AI answered "Max"
            if (string.Equals(token, "Max", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "LastChance", StringComparison.OrdinalIgnoreCase))
            {
                var maxOpt = options.TryGetValue("Max", out var m) ? m : fallback;
                var widestC = GetWidestCandidateTime(options, isStart, fallback);

                // 1. If Max is identical to the widest candidate option (because the remaining zone to Max was 100% silence
                //    or clamped by HardLimit), lock cleanly at widestC with no warning and no further expansion!
                if (maxOpt == widestC)
                {
                    return (widestC, true, false);
                }

                // 2. If Max is already clamped against a neighbor's HardLimit, lock cleanly at widestC
                if (!isStart && cand.HardLimitEnd.HasValue && maxOpt >= cand.HardLimitEnd.Value)
                {
                    return (widestC, true, false);
                }
                if (isStart && cand.HardLimitStart.HasValue && maxOpt <= cand.HardLimitStart.Value)
                {
                    return (widestC, true, false);
                }

                // 3. Never jump blindly into raw Max (+2.0s). If widestC did not advance beyond fallback (no dips existed),
                //    lock at widestC and flag Missing Start/End. Otherwise shift Center to widestC for the next round.
                if (widestC == fallback)
                {
                    return (widestC, true, true);
                }

                return (widestC, false, true);
            }

            // If AI wrote a hyphenated pair e.g. "C2-C3", prefer the tighter boundary rather than cutting in the middle of a mountain
            if (token.Contains("-"))
            {
                var pair = token.Split(new[] { '-' }, StringSplitOptions.RemoveEmptyEntries);
                if (pair.Length == 2 &&
                    TryLookupOptionTime(options, pair[0].Trim(), fallback, out var t1) &&
                    TryLookupOptionTime(options, pair[1].Trim(), fallback, out var t2))
                {
                    var tighter = isStart
                        ? (t1 > t2 ? t1 : t2)
                        : (t1 < t2 ? t1 : t2);
                    return (tighter, true, false);
                }
            }

            if (TryLookupOptionTime(options, token, fallback, out var resolved))
            {
                return (resolved, true, false);
            }

            return (fallback, true, false);
        }

        private static bool TryLookupOptionTime(Dictionary<string, TimeSpan> options, string key, TimeSpan centerFallback, out TimeSpan time)
        {
            if (options.TryGetValue(key, out time))
                return true;

            // Normalize common aliases if the AI ever outputs Init
            if (string.Equals(key, "0", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "Init", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "Initial", StringComparison.OrdinalIgnoreCase))
            {
                time = centerFallback;
                return true;
            }

            return false;
        }

        private TimeSpan ApplyOutwardNudge(TimeSpan t, BoundarySide side)
        {
            var nudged = side == BoundarySide.Start
                ? t - this.OutwardNudgeDuration
                : t + this.OutwardNudgeDuration;
            return ClampToAudioBounds(nudged);
        }

        private TimeSpan ClampToAudioBounds(TimeSpan t)
        {
            if (t < TimeSpan.Zero)
                return TimeSpan.Zero;
            if (t > this.AudioEndTime)
                return this.AudioEndTime;
            return t;
        }

        public void FinalizeOverlapsAndMinimumDuration()
        {
            var sorted = this.Candidates.OrderBy(c => c.OriginalStartTime).ToList();
            var minViableDuration = TimeSpan.FromSeconds(0.05);

            for (int i = 0; i < sorted.Count - 1; i++)
            {
                var cur = sorted[i];
                var next = sorted[i + 1];

                if (cur.CurrentTestEnd > next.CurrentTestStart)
                {
                    // If they overlap slightly (e.g. from +30ms nudge or shared valley), clamp at midpoint or next.CurrentTestStart
                    var overlap = cur.CurrentTestEnd - next.CurrentTestStart;
                    if (overlap <= TimeSpan.FromMilliseconds(150))
                    {
                        var mid = TimeSpan.FromMilliseconds((cur.CurrentTestEnd.TotalMilliseconds + next.CurrentTestStart.TotalMilliseconds) / 2.0);
                        if (mid - cur.CurrentTestStart >= minViableDuration && next.CurrentTestEnd - mid >= minViableDuration)
                        {
                            cur.CurrentTestEnd = mid;
                            next.CurrentTestStart = mid;
                            cur.AddTraceRow("Clmp", ' ', cur.CurrentTestStart, cur.CurrentTestEnd, '-', "Clamped minor overlap with next");
                            next.AddTraceRow("Clmp", '-', next.CurrentTestStart, next.CurrentTestEnd, ' ', "Clamped minor overlap with prev");
                            continue;
                        }
                    }

                    cur.AddValidationNote("Review: Overlaps with adjacent subtitle");
                    next.AddValidationNote("Review: Overlaps with adjacent subtitle");
                }
            }
        }
    }
}
