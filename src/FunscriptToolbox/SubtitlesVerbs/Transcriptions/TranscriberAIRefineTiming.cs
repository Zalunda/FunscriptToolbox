using FunscriptToolbox.Core;
using FunscriptToolbox.SubtitlesVerbs.AudioExtractions;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscriberAIRefineTiming : TranscriberAI
    {
        public enum MatchResult
        {
            FullMatch,
            FullExtra,
            MissingStart,
            MissingEnd,
            MissingBoth,
            Mismatch
        }

        public class JobState
        {
            public int Iteration { get; set; }
            public int TotalTestsRun { get; set; }

            public List<TimingCandidate> Candidates { get; set; } = new List<TimingCandidate>();
            public List<TimeSpan> ActiveBatchStartTimes { get; set; }
            public Transcription PendingBatch { get; set; }
        }

        public class TimingCandidate
        {
            public TimeSpan OriginalStartTime { get; set; }
            public TimeSpan OriginalEndTime { get; set; }
            public string OriginalText { get; set; }

            [JsonIgnore] public TimedItemWithMetadata OriginalItem { get; set; }
            
            public TimeSpan CurrentTestStart { get; set; }
            public TimeSpan CurrentTestEnd { get; set; }

            // Tracks the bounds of the previous row in the trace to compute '+', '-', or ' '
            public TimeSpan TracePrevStart { get; set; }
            public TimeSpan TracePrevEnd { get; set; }
            public char PendingStartMod { get; set; } = ' ';
            public char PendingEndMod { get; set; } = ' ';

            public TimeSpan PreviousTestStart { get; set; }
            public TimeSpan PreviousTestEnd { get; set; }
            public string LastVoiceTextHeardText { get; set; }

            public bool IsStartLocked { get; set; }
            public bool IsEndLocked { get; set; }
            [JsonIgnore] public bool IsFullyLocked => IsStartLocked && IsEndLocked;

            public int RetryCount { get; set; }
            public List<string> ValidationNotes { get; set; } = new List<string>();
            public List<string> Trace { get; set; } = new List<string>();

            public void AddValidationNote(string note)
            {
                if (!ValidationNotes.Contains(note))
                    ValidationNotes.Add(note);
            }

            public string GetLockTag()
            {
                if (IsStartLocked && IsEndLocked) return "**";
                if (IsStartLocked) return "S?";
                if (IsEndLocked) return "?E";
                return "??";
            }

            public static char GetStartMod(TimeSpan prev, TimeSpan cur)
            {
                var diffMs = (cur - prev).TotalMilliseconds;
                if (Math.Abs(diffMs) < 1) return ' ';
                return diffMs > 0 ? '-' : '+'; // Start moving right (>0) is inward (-), moving left (<0) is outward (+)
            }

            public static char GetEndMod(TimeSpan prev, TimeSpan cur)
            {
                var diffMs = (cur - prev).TotalMilliseconds;
                if (Math.Abs(diffMs) < 1) return ' ';
                return diffMs < 0 ? '-' : '+'; // End moving left (<0) is inward (-), moving right (>0) is outward (+)
            }

            public void AddTraceRow(string step, char startMod, TimeSpan start, TimeSpan end, char endMod, string result = null, string text = null)
            {
                string lockTag = GetLockTag();
                string stepCol = (step ?? "").PadRight(4);
                string boundsCol = $"[{startMod}{start:hh\\:mm\\:ss\\.fff}=>{end:hh\\:mm\\:ss\\.fff}{endMod}]";

                if (string.IsNullOrEmpty(result) && string.IsNullOrEmpty(text))
                {
                    Trace.Add($"{lockTag} {stepCol} {boundsCol}");
                }
                else
                {
                    string resCol = (result ?? "").PadRight(12);
                    string textCol = text != null ? $"'{text}'" : "";
                    Trace.Add($"{lockTag} {stepCol} {boundsCol} {resCol} {textCol}".TrimEnd());
                }
            }
        }

        [JsonProperty(Order = 34, Required = Required.Always)]
        public AIOptions InnerOptions { get; set; } = new AIOptions();

        [JsonProperty(Order = 43)]
        public int MaxRetriesPerNode { get; set; } = 3;

        [JsonProperty(Order = 44)]
        public double MaxBudgetRatio { get; set; } = 3.0;

        [JsonProperty(Order = 45)]
        public double MaxDriftSeconds { get; set; } = 0.6;

        [JsonProperty(Order = 46)]
        public double ShrinkStepSeconds { get; set; } = 0.2;

        [JsonProperty(Order = 47)]
        public double MinSubtitleDurationSeconds { get; set; } = 0.5;


        protected override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            foreach (var extractor in this.InnerOptions.BinaryDataExtractors?.OfType<BinaryDataExtractorAudio>() ?? Array.Empty<BinaryDataExtractorAudio>())
            {
                if (context.WIP.AudioExtractions.FirstOrDefault(f => f.Id == extractor.SourceAudioId && f.IsFinished) == null)
                {
                    reason = $"Audio extraction '{extractor.SourceAudioId}' is not done yet.";
                    return false;
                }
            }
            return base.IsPrerequisitesMet(context, out reason);
        }

        protected override void DoWorkInternal(SubtitleGeneratorContext context, Transcription transcription)
        {
            var jobState = transcription.CurrentJobState as JobState ?? new JobState();
            transcription.CurrentJobState = jobState;

            var audioExtractor = this.InnerOptions.BinaryDataExtractors.OfType<BinaryDataExtractorAudio>().FirstOrDefault();
            var fullPcmAudio = context.WIP.AudioExtractions.FirstOrDefault(f => f.Id == audioExtractor?.SourceAudioId && f.IsFinished)?.PcmAudio;

            if (fullPcmAudio == null)
            {
                context.WriteError("Cannot refine timing: Full PCM audio extraction not found or not finished.");
                return;
            }

            var aggregation = this.Metadatas?.Aggregate(context);
            var originalItems = (aggregation.ReferenceTimingsWithMetadata ?? Array.Empty<TimedItemWithMetadata>())
                .OrderBy(f => f.StartTime)
                .ToList();

            if (originalItems.Count == 0)
            {
                transcription.MarkAsFinished();
                context.WIP.Save();
                return;
            }

            if (jobState.Candidates.Count == 0)
            {
                context.WriteInfo($"[RefineTiming] Phase 1: Initializing acoustic bounds on {originalItems.Count} candidates...");
                jobState.Candidates = InitializeCandidates(fullPcmAudio, originalItems);
                DoExportTraceSrt(context, transcription, jobState.Candidates, "FIRST-INIT");
            }
            else
            {
                foreach (var item in originalItems)
                {
                    var cand = jobState.Candidates.FirstOrDefault(c => c.OriginalStartTime == item.StartTime);
                    if (cand != null)
                    {
                        cand.OriginalItem = item;
                    }
                }
            }

            int totalAllowedTests = Math.Max(50, (int)(originalItems.Count * MaxBudgetRatio));

            while (jobState.TotalTestsRun < totalAllowedTests)
            {
                var pendingCandidates = jobState.Candidates
                    .Where(c => !c.IsFullyLocked && c.RetryCount < MaxRetriesPerNode)
                    .ToList();

                if (pendingCandidates.Count == 0 && jobState.PendingBatch == null)
                    break;

                List<TimingCandidate> batchToTest;

                if (jobState.PendingBatch == null)
                {
                    jobState.Iteration++;
                    context.WriteInfo($"[RefineTiming] Iteration {jobState.Iteration}: Testing {pendingCandidates.Count} candidate timings (Total tests: {jobState.TotalTestsRun}/{totalAllowedTests})...");

                    batchToTest = SelectCandidatesForBatch(pendingCandidates);
                    if (batchToTest.Count == 0)
                        break;

                    jobState.ActiveBatchStartTimes = batchToTest.Select(c => c.OriginalStartTime).ToList();

                    string fakeId = $"{transcription.Id}_refine_iter_{jobState.Iteration}";
                    jobState.PendingBatch = new Transcription(
                        id: fakeId,
                        metadataAlwaysProduced: this.InnerOptions.MetadataAlwaysProduced,
                        privateMetadataNames: null,
                        language: transcription.Language,
                        isFinished: false);

                    context.WIP.Save();
                }
                else
                {
                    context.WriteInfo($"[RefineTiming] Resuming interrupted Iteration {jobState.Iteration}...");
                    batchToTest = jobState.ActiveBatchStartTimes
                        .Select(origStart => jobState.Candidates.First(c => c.OriginalStartTime == origStart))
                        .ToList();
                }

                if (batchToTest.Count > 0)
                {
                    var itemsToBeProcessed = batchToTest
                        .Select(cand => new TimedItemWithMetadata(cand.CurrentTestStart, cand.CurrentTestEnd, new MetadataCollection()))
                        .ToArray();

                    var requestGenerator = ExecuteRequests(context, jobState.PendingBatch, this.InnerOptions, itemsToBeProcessed);

                    if (!requestGenerator.IsFinished())
                        return; // Interrupted
                }

                jobState.TotalTestsRun += jobState.PendingBatch.Items.Count;
                ProcessBatchResults(context, batchToTest, jobState.PendingBatch, fullPcmAudio);
                DoExportTraceSrt(context, transcription, jobState.Candidates, "TRACE");

                transcription.Costs.AddRange(jobState.PendingBatch.Costs);
                jobState.PendingBatch = null;
                jobState.ActiveBatchStartTimes = null;
                context.WIP.Save();

                int lockedNodes = jobState.Candidates.Count(c => c.IsFullyLocked);
                context.WriteInfo($"[RefineTiming] Progress: {lockedNodes}/{jobState.Candidates.Count} fully locked.");
            }

            FinalizeTranscription(transcription, jobState.Candidates);
            transcription.MarkAsFinished();
            context.WIP.Save();
            context.WriteInfo($"[RefineTiming] Refinement completed ({jobState.TotalTestsRun} tests run).");
        }

        private List<TimingCandidate> InitializeCandidates(PcmAudio fullPcmAudio, List<TimedItemWithMetadata> originalItems)
        {
            var candidates = new List<TimingCandidate>();

            foreach (var item in originalItems)
            {
                var cand = new TimingCandidate
                {
                    OriginalStartTime = item.StartTime,
                    OriginalEndTime = item.EndTime,
                    OriginalItem = item,
                    OriginalText = item.Metadata.TryGetValue(this.InnerOptions.MetadataNeeded, out var ct) ? ct : string.Empty,
                    CurrentTestStart = item.StartTime,
                    CurrentTestEnd = item.EndTime,
                    PreviousTestStart = item.StartTime,
                    PreviousTestEnd = item.EndTime
                };

                var terrain = CreateCandidateTerrain(fullPcmAudio, cand);
                var cursor = terrain.From(cand.OriginalStartTime, cand.OriginalEndTime);

                // 1. Refine Start
                if (cursor.Start.MoveIfSilent(BoundaryDirection.Inward))
                {
                    cursor.Start.MoveTowardSpeech(BoundaryDirection.Inward);
                }
                else
                {
                    cursor.Start.SlideDownSlopeToStableValley(BoundaryDirection.Outward);
                }

                // 2. Refine End
                if (cursor.End.MoveIfSilent(BoundaryDirection.Inward))
                {
                    cursor.End.MoveTowardSpeech(BoundaryDirection.Inward);
                }
                else
                {
                    cursor.End.SlideDownSlopeToStableValley(BoundaryDirection.Outward);
                }

                cursor.EnforceMinimumDuration(TimeSpan.FromSeconds(MinSubtitleDurationSeconds));

                cand.CurrentTestStart = cursor.Start.Position;
                cand.CurrentTestEnd = cursor.End.Position;
                cand.PreviousTestStart = cand.CurrentTestStart;
                cand.PreviousTestEnd = cand.CurrentTestEnd;

                // Log Orig row immediately
                cand.AddTraceRow("Orig", ' ', item.StartTime, item.EndTime, ' ', "", cand.OriginalText);

                // Calculate how Init moved compared to Orig
                cand.PendingStartMod = TimingCandidate.GetStartMod(item.StartTime, cand.CurrentTestStart);
                cand.PendingEndMod = TimingCandidate.GetEndMod(item.EndTime, cand.CurrentTestEnd);

                candidates.Add(cand);
            }

            return candidates;
        }

        private void ProcessBatchResults(
            SubtitleGeneratorContext context,
            List<TimingCandidate> batch,
            Transcription fakeTranscription,
            PcmAudio fullPcmAudio)
        {
            foreach (var fakeItem in fakeTranscription.Items)
            {
                var cand = batch.FirstOrDefault(c => c.CurrentTestStart == fakeItem.StartTime);
                if (cand == null) continue;

                cand.RetryCount++;
                string voiceTextHeard = fakeItem.Metadata.TryGetValue(this.Options.MetadataAlwaysProduced, out var h) ? h : string.Empty;
                string normalizedVoiceTextHeard = Normalize(voiceTextHeard);

                var match = EvaluateMatch(cand.OriginalText, voiceTextHeard);

                bool heardUnchanged = !string.IsNullOrEmpty(cand.LastVoiceTextHeardText)
                                      && cand.LastVoiceTextHeardText == normalizedVoiceTextHeard;

                cand.LastVoiceTextHeardText = normalizedVoiceTextHeard;

                string stepName = cand.RetryCount == 1 ? "Init" : $"#{cand.RetryCount - 1}";
                char testedStartMod = cand.PendingStartMod;
                char testedEndMod = cand.PendingEndMod;

                TimeSpan nextStart = cand.CurrentTestStart;
                TimeSpan nextEnd = cand.CurrentTestEnd;
                bool reverted = false;

                switch (match)
                {
                    case MatchResult.FullMatch:
                        cand.IsStartLocked = true;
                        cand.IsEndLocked = true;
                        cand.ValidationNotes.Clear();
                        break;

                    case MatchResult.FullExtra:
                        cand.IsStartLocked = true;
                        cand.IsEndLocked = true;
                        cand.ValidationNotes.Clear();
                        cand.AddValidationNote($"Review: Too Much Text Found , Timing Could Be Tightened or expended to include part that was missed by transcriber: {voiceTextHeard}");
                        break;

                    case MatchResult.MissingStart:
                        cand.IsEndLocked = true;

                        if (cand.IsStartLocked)
                        {
                            // Start was already verified in a prior pass, and End was just verified now!
                            cand.ValidationNotes.Clear();
                        }
                        else if (heardUnchanged && cand.RetryCount > 1)
                        {
                            nextStart = cand.PreviousTestStart;
                            cand.IsStartLocked = true;
                            reverted = true;
                            cand.AddValidationNote("Unsure: Missing Start");
                        }
                        else if (cand.RetryCount >= MaxRetriesPerNode)
                        {
                            cand.IsStartLocked = true;
                            cand.AddValidationNote("Unsure: Missing Start");
                        }
                        else
                        {
                            cand.PreviousTestStart = cand.CurrentTestStart;
                            nextStart = ExpandBoundary(fullPcmAudio, cand, BoundaryPointType.Start);
                        }
                        break;

                    case MatchResult.MissingEnd:
                        cand.IsStartLocked = true;

                        if (cand.IsEndLocked)
                        {
                            // End was already verified in a prior pass, and Start was just verified now!
                            cand.ValidationNotes.Clear();
                        }
                        else if (heardUnchanged && cand.RetryCount > 1)
                        {
                            nextEnd = cand.PreviousTestEnd;
                            cand.IsEndLocked = true;
                            reverted = true;
                            cand.AddValidationNote("Unsure: Missing End");
                        }
                        else if (cand.RetryCount >= MaxRetriesPerNode)
                        {
                            cand.IsEndLocked = true;
                            cand.AddValidationNote("Unsure: Missing End");
                        }
                        else
                        {
                            cand.PreviousTestEnd = cand.CurrentTestEnd;
                            nextEnd = ExpandBoundary(fullPcmAudio, cand, BoundaryPointType.End);
                        }
                        break;

                    case MatchResult.MissingBoth:
                        if (heardUnchanged && cand.RetryCount > 1)
                        {
                            nextStart = cand.PreviousTestStart;
                            nextEnd = cand.PreviousTestEnd;
                            cand.IsStartLocked = cand.IsEndLocked = true;
                            reverted = true;
                            cand.AddValidationNote("Unsure: Clipped Both Sides");
                        }
                        else if (cand.RetryCount >= MaxRetriesPerNode)
                        {
                            cand.IsStartLocked = cand.IsEndLocked = true;
                            cand.AddValidationNote("Unsure: Clipped Both Sides");
                        }
                        else
                        {
                            cand.PreviousTestStart = cand.CurrentTestStart;
                            cand.PreviousTestEnd = cand.CurrentTestEnd;
                            nextStart = ExpandBoundary(fullPcmAudio, cand, BoundaryPointType.Start);
                            nextEnd = ExpandBoundary(fullPcmAudio, cand, BoundaryPointType.End);
                        }
                        break;

                    case MatchResult.Mismatch:
                        cand.IsStartLocked = cand.IsEndLocked = true;
                        string note = string.IsNullOrWhiteSpace(voiceTextHeard) ? "Unsure: Audio Inaudible" : $"Unsure: Text Mismatch: {voiceTextHeard}";
                        cand.AddValidationNote(note);
                        break;
                }

                // 1. Log the test row that just ran
                cand.AddTraceRow(
                    stepName,
                    testedStartMod,
                    cand.CurrentTestStart,
                    cand.CurrentTestEnd,
                    testedEndMod,
                    match.ToString(),
                    voiceTextHeard);

                // 2. If we reverted because expansion heard nothing new, log the 'Rev' row with +/-
                if (reverted)
                {
                    char revStartMod = TimingCandidate.GetStartMod(cand.CurrentTestStart, nextStart);
                    char revEndMod = TimingCandidate.GetEndMod(cand.CurrentTestEnd, nextEnd);
                    cand.AddTraceRow("Rev", revStartMod, nextStart, nextEnd, revEndMod);
                }
                else if (!cand.IsFullyLocked)
                {
                    // Prepare the +/- modifiers for the next test iteration
                    cand.PendingStartMod = TimingCandidate.GetStartMod(cand.CurrentTestStart, nextStart);
                    cand.PendingEndMod = TimingCandidate.GetEndMod(cand.CurrentTestEnd, nextEnd);
                }

                cand.CurrentTestStart = nextStart;
                cand.CurrentTestEnd = nextEnd;
            }
        }

        #region EvaluateMatch

        private static readonly Regex PunctuationNormalizeRegex = new Regex(@"[.!…]+", RegexOptions.Compiled);
        private static readonly Regex WhitespaceNormalizeRegex = new Regex(@"\s+", RegexOptions.Compiled);

        private MatchResult EvaluateMatch(string candidateText, string heardText)
        {
            if (string.IsNullOrWhiteSpace(heardText))
                return MatchResult.Mismatch;

            string cNorm = Normalize(candidateText);
            string hNorm = Normalize(heardText);

            if (cNorm.Length == 0 || hNorm.Length == 0)
                return MatchResult.Mismatch;

            if (cNorm == hNorm)
                return MatchResult.FullMatch;

            string cTrim = cNorm.TrimEnd('.');
            string hTrim = hNorm.TrimEnd('.');

            if (cTrim.Length == 0 || hTrim.Length == 0 || cTrim.TrimEnd('?') == hTrim.TrimEnd('?'))
                return MatchResult.Mismatch;

            if (hTrim.Contains(cTrim))
                return MatchResult.FullExtra;

            if (cTrim.StartsWith(hTrim))
                return MatchResult.MissingEnd;

            if (cTrim.EndsWith(hTrim))
                return MatchResult.MissingStart;

            if (cTrim.Contains(hTrim))
                return MatchResult.MissingBoth;

            return MatchResult.Mismatch;
        }

        private string Normalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            string s = text.Trim().ToLowerInvariant();
            s = PunctuationNormalizeRegex.Replace(s, ".");
            s = WhitespaceNormalizeRegex.Replace(s, " ");
            return s;
        }

        #endregion

        private AudioTerrain CreateCandidateTerrain(PcmAudio fullAudio, TimingCandidate cand)
        {
            var maxDrift = TimeSpan.FromSeconds(MaxDriftSeconds);
            TimeSpan padStart = cand.OriginalStartTime - maxDrift;
            TimeSpan padEnd = cand.OriginalEndTime + maxDrift;

            return new AudioTerrain(fullAudio, cand.OriginalStartTime, cand.OriginalEndTime, padStart, padEnd);
        }

        private TimeSpan ExpandBoundary(PcmAudio fullAudio, TimingCandidate cand, BoundaryPointType pointType)
        {
            var terrain = CreateCandidateTerrain(fullAudio, cand);
            var endpoint = terrain.From(cand.CurrentTestStart, cand.CurrentTestEnd)[pointType];

            endpoint.Step(BoundaryDirection.Outward, TimeSpan.FromSeconds(ShrinkStepSeconds));
            endpoint.SeekStableGround(BoundaryDirection.Outward);

            return endpoint.Position;
        }

        private List<TimingCandidate> SelectCandidatesForBatch(List<TimingCandidate> pendingCandidates)
        {
            var batch = new List<TimingCandidate>();
            TimingCandidate lastSelected = null;

            foreach (var cand in pendingCandidates)
            {
                if (lastSelected != null && (cand.OriginalStartTime - lastSelected.OriginalEndTime).TotalMilliseconds < 250)
                {
                    if (lastSelected.IsFullyLocked)
                        continue;
                }

                batch.Add(cand);
                lastSelected = cand;
            }

            return batch;
        }

        private void FinalizeTranscription(
            Transcription transcription,
            List<TimingCandidate> candidates)
        {
            MediateOverlappingCandidates(candidates);

            var newItems = new List<TranscribedItem>();

            foreach (var cand in candidates)
            {
                TimeSpan finalStart = cand.CurrentTestStart;
                TimeSpan finalEnd = cand.CurrentTestEnd;

                var meta = new MetadataCollection();
                foreach (var kvp in cand.OriginalItem.Metadata)
                {
                    meta[kvp.Key] = kvp.Value;
                }

                var sb = new StringBuilder();

                // Clean nodes have no TimingValidation key, keeping the subtitle clean.
                if (cand.ValidationNotes.Count > 0 || !cand.IsFullyLocked)
                {
                    sb.AppendLine("Timings Warnings:");
                    // If global test budget ran out before this candidate finished testing
                    if (!cand.IsFullyLocked)
                    {
                        sb.AppendLine("Unsure: Unfinished / Budget Reached");
                    }
                    foreach (var note in cand.ValidationNotes)
                    {
                        sb.AppendLine("  " + note);
                    }
                }

                if (sb.Length > 0)
                {
                    sb.AppendLine();
                }
                sb.AppendLine(cand.OriginalText);

                meta[this.Options.MetadataAlwaysProduced] = sb.ToString();
                meta["TimingTrace"] = string.Join("\n", cand.Trace);

                var refinedItem = new TranscribedItem(
                    finalStart,
                    finalEnd,
                    meta,
                    (cand.OriginalItem as TranscribedItem)?.NoSpeechProbability ?? 0.0,
                    (cand.OriginalItem as TranscribedItem)?.Words);

                newItems.Add(refinedItem);
            }

            transcription.Items.Clear();
            transcription.Items.AddRange(newItems);
        }

        private void MediateOverlappingCandidates(List<TimingCandidate> candidates)
        {
            var sorted = candidates
                .OrderBy(c => c.CurrentTestStart)
                .ToList();

            var minDuration = TimeSpan.FromSeconds(MinSubtitleDurationSeconds);

            for (int i = 0; i < sorted.Count - 1; i++)
            {
                var cur = sorted[i];
                var next = sorted[i + 1];

                var curStart = cur.CurrentTestStart;
                var curEnd = cur.CurrentTestEnd;

                var nextStart = next.CurrentTestStart;
                var nextEnd = next.CurrentTestEnd;

                if (curEnd > nextStart)
                {
                    var overlapDuration = curEnd - nextStart;

                    var midTicks = (curEnd.Ticks + nextStart.Ticks) / 2;
                    var midpoint = TimeSpan.FromTicks(midTicks);

                    if (midpoint - curStart < minDuration)
                    {
                        midpoint = curStart + minDuration;
                    }
                    else if (nextEnd - midpoint < minDuration)
                    {
                        midpoint = nextEnd - minDuration;
                    }

                    if (midpoint <= curStart || midpoint >= nextEnd)
                    {
                        midpoint = TimeSpan.FromTicks(midTicks);
                    }

                    char curEndMod = TimingCandidate.GetEndMod(curEnd, midpoint);
                    char nextStartMod = TimingCandidate.GetStartMod(nextStart, midpoint);

                    cur.CurrentTestEnd = midpoint;
                    next.CurrentTestStart = midpoint;

                    // Only flag for manual review if the overlap was meaningful (>= 50ms)
                    if (overlapDuration.TotalMilliseconds >= 50)
                    {
                        cur.AddValidationNote("Review: Shared Audio Zone");
                        next.AddValidationNote("Review: Shared Audio Zone");

                        cur.AddTraceRow("Med", ' ', cur.CurrentTestStart, cur.CurrentTestEnd, curEndMod);
                        next.AddTraceRow("Med", nextStartMod, next.CurrentTestStart, next.CurrentTestEnd, ' ');
                    }
                }
            }
        }

        private void DoExportTraceSrt(
            SubtitleGeneratorContext context,
            Transcription transcription,
            IEnumerable<TimingCandidate> candidates,
            string name)
        {
            if (this.ExportMetadataSrt)
            {
                var extension = $".Worker.{transcription.Id}.{name}.srt";
                var virtualSubtitleFile = context.WIP.CreateVirtualSubtitleFile();
                virtualSubtitleFile.Subtitles.AddRange(candidates.Select(cand => new Subtitle(cand.CurrentTestStart, cand.CurrentTestEnd, cand.Trace.ToArray())));
                virtualSubtitleFile.Save(context.WIP.ParentPath, extension, context.SoftDelete);

                var fullfilePath = context.WIP.BaseFilePath + extension;
                if (context.WIP.TimelineMap.IsMultipart)
                {
                    var newFullFile = new SubtitleFile();
                    newFullFile.Subtitles.AddRange(
                        virtualSubtitleFile.Subtitles.Select(item =>
                            new Subtitle(item.StartTime, item.EndTime, item.Text)));
                    newFullFile.SaveSrt(fullfilePath);
                }
            }
        }
    }
}