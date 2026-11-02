using FunscriptToolbox.Core;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    /// <summary>
    /// Prepares the subtitles of a transcription (full-ai) for the manual-input file: the nodes that are clean, and the ones
    /// that need the user's attention ([REVIEW] block). It does up to three things, each with its own settings and Enabled
    /// (all disabled: the result is a clone of the source):
    ///   - RefineTiming: refines the timing of each subtitle in two sequential passes:
    ///       1. START pass: Audio_Max, Audio_S1..Audio_Sn (same end, different starts) -> BestStart
    ///       2. END pass:   Audio_Max, Audio_E1..Audio_En (start locked, different ends) -> BestEnd
    ///     Each pass has its own prompt/options (InnerOptionsStart / InnerOptionsEnd).
    ///   - Validation: checks the refined subtitles, flags what needs a look and splits the long ones.
    ///   - TitleCardDetection: adds a node for each title card found in the videos.
    /// </summary>
    public class TranscriberAIManualInputPreparer : TranscriberAI
    {
        // ---------------------------------------------------------------------
        // Cache
        // ---------------------------------------------------------------------

        public class CachedCandidateResponse
        {
            public string BestStart { get; set; }
            public string BestEnd { get; set; }
            public string HeardText { get; set; }

            public bool IsEmpty => BestStart == null && BestEnd == null && HeardText == null;

            public static CachedCandidateResponse FromMetadata(MetadataCollection metadata)
            {
                metadata.TryGetValue(TimingMeta.BestStart, out var bestStart);
                metadata.TryGetValue(TimingMeta.BestEnd, out var bestEnd);
                metadata.TryGetValue(TimingMeta.HeardText, out var heardText);
                return new CachedCandidateResponse
                {
                    BestStart = bestStart,
                    BestEnd = bestEnd,
                    HeardText = heardText
                };
            }

            public MetadataCollection ToMetadata()
            {
                var metadata = new MetadataCollection();
                if (BestStart != null) metadata[TimingMeta.BestStart] = BestStart;
                if (BestEnd != null) metadata[TimingMeta.BestEnd] = BestEnd;
                if (HeardText != null) metadata[TimingMeta.HeardText] = HeardText;
                return metadata;
            }
        }

        public class JobState
        {
            public Dictionary<string, CachedCandidateResponse> CachedResults = new Dictionary<string, CachedCandidateResponse>();

            /// <summary>By filename of the video.</summary>
            public Dictionary<string, TitleCardProgress> TitleCards = new Dictionary<string, TitleCardProgress>();

            public static string BuildKey(TimeSpan originalStart, TimingRound round)
            {
                var clipsSig = string.Join(";", round.Clips.Select(c => $"{c.OptionId}:{c.Start:hh\\:mm\\:ss\\.fff}-{c.End:hh\\:mm\\:ss\\.fff}"));
                return $"{originalStart:hh\\:mm\\:ss\\.fff}|{round.Side}|{round.Center:hh\\:mm\\:ss\\.fff}|{StableHash(round.TextSent)}|{clipsSig}";
            }

            /// <summary>64-bit FNV-1a (string.GetHashCode is randomized per process on modern .NET, so it can't be used for a persisted cache).</summary>
            private static string StableHash(string text)
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

            public void AddCachedResult(TimeSpan originalStart, TimingRound round, CachedCandidateResponse result)
            {
                CachedResults[BuildKey(originalStart, round)] = result;
            }

            public bool TryGetCachedResult(TimeSpan originalStart, TimingRound round, out CachedCandidateResponse result)
            {
                return CachedResults.TryGetValue(BuildKey(originalStart, round), out result);
            }
        }

        // ---------------------------------------------------------------------
        // Multi-clip extractor
        // ---------------------------------------------------------------------

        private class MultiClipBinaryDataExtractorCollection : BinaryDataExtractorCachedCollection
        {
            private readonly Dictionary<TimeSpan, List<TimingClipOption>> r_clipsByStartTime;

            public MultiClipBinaryDataExtractorCollection(
                BinaryDataExtractorExtended[] extractors,
                Dictionary<TimeSpan, List<TimingClipOption>> clipsByStartTime)
                : base(extractors)
            {
                r_clipsByStartTime = clipsByStartTime ?? new Dictionary<TimeSpan, List<TimingClipOption>>();
            }

            public override Dictionary<string, AIRequestPart[]> GetNamedContentListForItem(
                AIRequestSection section,
                TimedItemWithMetadata item,
                string text = null,
                bool silent = false)
            {
                var audioExtractor = r_extractors.Values.FirstOrDefault(e => e.Extractor is BinaryDataExtractorAudio);
                if (audioExtractor != null && r_clipsByStartTime.TryGetValue(item.StartTime, out var clips) && clips != null && clips.Count > 0)
                {
                    var result = new Dictionary<string, AIRequestPart[]>();
                    foreach (var clip in clips)
                    {
                        var timing = new Timing(clip.Start, clip.End);
                        var clipData = GetNamedContentListForTiming(section, timing, text, silent);
                        if (clipData.TryGetValue(audioExtractor.Extractor.OutputFieldName, out var parts))
                        {
                            result[clip.FieldName] = parts;
                        }
                    }

                    // Also include any non-audio extractors if configured
                    foreach (var kvp in r_extractors.Where(e => !(e.Value.Extractor is BinaryDataExtractorAudio)))
                    {
                        if (kvp.Value.Extractor.MetadataForSkipping == null || !item.Metadata.ContainsKey(kvp.Value.Extractor.MetadataForSkipping))
                        {
                            result[kvp.Key] = kvp.Value.GetData(section, item, text, silent);
                        }
                    }

                    return result;
                }

                return base.GetNamedContentListForItem(section, item, text, silent);
            }
        }

        private Dictionary<TimeSpan, List<TimingClipOption>> m_activeClipsByStartTime;

        protected override BinaryDataExtractorCachedCollection CreateBinaryDataExtractorCollection(
            BinaryDataExtractorExtended[] extractors)
        {
            if (m_activeClipsByStartTime != null)
            {
                return new MultiClipBinaryDataExtractorCollection(extractors, m_activeClipsByStartTime);
            }
            return base.CreateBinaryDataExtractorCollection(extractors);
        }

        // ---------------------------------------------------------------------
        // Settings: the task does up to three things, each with its own settings and its own Enabled.
        // All three disabled: the output is a clone of the source (full-ai).
        // ---------------------------------------------------------------------

        [JsonProperty(Order = 27)]
        public bool IncludeTraceInVoiceTextForManualReviewAlways { get; set; } = false;

        [JsonProperty(Order = 28)]
        public bool IncludeTraceInVoiceTextForManualReviewOnError { get; set; } = true;

        [JsonProperty(Order = 29)]
        public bool IncludeTextInVoiceTextForManualReview { get; set; } = true;

        /// <summary>
        /// Looks for title cards (text on a black background) in the videos, in the background, and adds a node for each
        /// (with {GrabOnScreenText}, so that its on-screen text is read later). Not set (null) or not Enabled: not done.
        /// </summary>
        [JsonProperty(Order = 30)]
        public TitleCardDetectorOptions TitleCardDetection { get; set; }


        /// <summary>Refines the Start and End of each subtitle (AI + terrain of the audio).</summary>
        [JsonProperty(Order = 31)]
        public TimingRefinerSettings RefineTiming { get; set; } = new TimingRefinerSettings();

        /// <summary>
        /// Checks the refined subtitles (and fixes some of the problems). Needs RefineTiming.
        /// Its Validations / Checks settings are used while refining too (what is flagged, the similarity of the texts).
        /// </summary>
        [JsonProperty(Order = 32)]
        public TimingValidatorSettings Validation { get; set; } = new TimingValidatorSettings();

        private bool IsTitleCardDetectionEnabled => this.TitleCardDetection?.Enabled == true;

        /// <summary>A clear error when the settings can't work (a missing setting, or a combination that makes no sense).</summary>
        private void ValidateConfiguration()
        {
            if (this.RefineTiming == null)
                throw new InvalidOperationException("The 'RefineTiming' settings are missing (use {\"Enabled\": false} to not refine the timings).");
            if (this.Validation == null)
                throw new InvalidOperationException("The 'Validation' settings are missing (use {\"Enabled\": false} to not validate).");

            if (this.Validation.Enabled && !this.RefineTiming.Enabled)
            {
                throw new InvalidOperationException(
                    "Validation is enabled but RefineTiming Mode is 'Off': the validation needs the terrain of the audio (Mode SnapOnly or more). " +
                    "Change the Mode or disable Validation.");
            }

            this.RefineTiming.Validate();
            this.Validation.Validate();
        }

        // ---------------------------------------------------------------------
        // Prerequisites
        // ---------------------------------------------------------------------

        protected override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            if (this.RefineTiming?.Enabled == true)
            {
                var terrainId = this.RefineTiming.SourceAudioIdForTerrain;
                if (context.WIP.AudioExtractions.Any(f => f.Id == terrainId)
                    && context.WIP.AudioExtractions.FirstOrDefault(f => f.Id == terrainId && f.IsFinished) == null)
                {
                    reason = $"Audio extraction '{terrainId}' is not done yet.";
                    return false;
                }

                foreach (var inner in this.RefineTiming.InnerOptionsUsed)
                {
                    foreach (var extractor in inner?.BinaryDataExtractors?.OfType<BinaryDataExtractorAudio>() ?? Array.Empty<BinaryDataExtractorAudio>())
                    {
                        if (context.WIP.AudioExtractions.FirstOrDefault(f => f.Id == extractor.SourceAudioId && f.IsFinished) == null)
                        {
                            reason = $"Audio extraction '{extractor.SourceAudioId}' is not done yet.";
                            return false;
                        }
                    }
                }
            }
            return base.IsPrerequisitesMet(context, out reason);
        }

        // ---------------------------------------------------------------------
        // Main loop
        // ---------------------------------------------------------------------

        /// <summary>Everything needed to send one iteration to the AI.</summary>
        private class RefineBatch
        {
            public Transcription Transcription;
            public List<TimedItemWithMetadata> ItemsToProcess = new List<TimedItemWithMetadata>();
            public Dictionary<TimeSpan, List<TimingClipOption>> ClipsByStartTime = new Dictionary<TimeSpan, List<TimingClipOption>>();

            /// <summary>Candidates really sent to the AI (not answered from the cache), by OriginalStartTime.</summary>
            public Dictionary<TimeSpan, TimingCandidate> NewlyRequested = new Dictionary<TimeSpan, TimingCandidate>();
        }

        private class PhaseReport
        {
            public BoundarySide Side;
            public int Iterations;
            public int Tests;     // field (not property) so it can be passed by ref
            public int FromCache; // the Tests that were answered by the cache
            public TimeSpan ClipDuration; // total length of all the audio clips of the Tests (Max and every option)
        }

        /// <summary>A subtitle given to the refiner, and where it comes from.</summary>
        private class RefineInput
        {
            public TimedItemWithMetadata Item;
            public TimingOrigin Origin;

            /// <summary>The edge is already validated (outer edge of a split subtitle).</summary>
            public TimeSpan? FixedStart;
            public TimeSpan? FixedEnd;
        }

        protected override void DoWorkInternal(SubtitleGeneratorContext context, Transcription transcription)
        {
            ValidateConfiguration();

            var jobState = GetOrCreateJobState(transcription);

            TitleCardRun titleCardRun = null;
            try
            {
                titleCardRun = StartTitleCardDetection(context, jobState);
                RefineAndExport(context, transcription, jobState, titleCardRun);
            }
            finally
            {
                // Interrupted or failed: the detection must not go on (what it found is in the job state)
                if (titleCardRun != null)
                {
                    titleCardRun.Cancellation.Cancel();
                    try { titleCardRun.Task.Wait(TimeSpan.FromSeconds(10)); } catch (AggregateException) { }
                    titleCardRun.Cancellation.Dispose();
                }
            }
        }

        // ---------------------------------------------------------------------
        // Title cards: detected in the background, spliced in at the end
        // ---------------------------------------------------------------------

        /// <summary>What the title card detection did on one video: it is saved in the job state, to resume where it stopped.</summary>
        public class TitleCardProgress
        {
            /// <summary>The settings used: when they change, the video is done again.</summary>
            public string OptionsSignature;

            /// <summary>Everything before this time (in the video) is done and in Segments.</summary>
            public TimeSpan ProcessedUntil;

            public bool Done;

            /// <summary>The cards found, in the time of the video.</summary>
            public List<TitleCardSegment> Segments = new List<TitleCardSegment>();
        }

        /// <summary>The title card detection running in the background, and what the main thread needs to follow it.</summary>
        private class TitleCardRun
        {
            private long m_processedTicks;
            private int m_found;

            /// <summary>Gives the title cards in the time of the whole timeline (the ones found so far, when stopped).</summary>
            public Task<List<TitleCardSegment>> Task;

            public CancellationTokenSource Cancellation = new CancellationTokenSource();

            /// <summary>The length of the whole timeline.</summary>
            public TimeSpan Total;

            /// <summary>True when the user stopped the detection (what is after Processed is not scanned).</summary>
            public volatile bool StoppedByUser;

            /// <summary>How far the detection went in the whole timeline.</summary>
            public TimeSpan Processed => TimeSpan.FromTicks(Interlocked.Read(ref m_processedTicks));

            public int Found => Volatile.Read(ref m_found);

            public void Report(TimeSpan processed, int found)
            {
                Interlocked.Exchange(ref m_processedTicks, processed.Ticks);
                Volatile.Write(ref m_found, found);
            }
        }

        /// <returns>null when the detection is not turned on.</returns>
        private TitleCardRun StartTitleCardDetection(SubtitleGeneratorContext context, JobState jobState)
        {
            if (!IsTitleCardDetectionEnabled)
                return null;

            var options = this.TitleCardDetection;

            // Fails right here (and not later in the background) when ffmpeg or the options are not configured
            var detector = new TitleCardDetector(context.FfmpegHelper.FfmpegPath, options);
            var signature = JsonConvert.SerializeObject(options);
            var timelineSegments = context.WIP.TimelineMap.Segments;
            var parentPath = context.WIP.ParentPath;

            var run = new TitleCardRun { Total = context.WIP.TimelineMap.Duration };
            var ct = run.Cancellation.Token;

            context.WriteInfo("  Starting title card detection (in the background)...");
            run.Task = Task.Run(async () =>
            {
                var cards = new List<TitleCardSegment>();
                foreach (var timelineSegment in timelineSegments)
                {
                    // What the background thread changes in the job state is changed while no save is in progress
                    TitleCardProgress progress = null;
                    context.WIP.ExecuteLocked(() =>
                    {
                        if (!jobState.TitleCards.TryGetValue(timelineSegment.Filename, out progress) || progress.OptionsSignature != signature)
                        {
                            progress = new TitleCardProgress { OptionsSignature = signature };
                            jobState.TitleCards[timelineSegment.Filename] = progress;
                        }
                    });

                    // Resuming: the progress shown starts from where the previous run stopped
                    run.Report(timelineSegment.Offset + progress.ProcessedUntil, cards.Count + progress.Segments.Count);

                    if (!progress.Done)
                    {
                        try
                        {
                            await detector.DetectAsync(
                                Path.Combine(parentPath, timelineSegment.Filename),
                                progress.ProcessedUntil,
                                (found, processedUntil) =>
                                {
                                    context.WIP.ExecuteLocked(() =>
                                    {
                                        progress.Segments.AddRange(found);
                                        progress.ProcessedUntil = processedUntil;
                                    });
                                    run.Report(timelineSegment.Offset + processedUntil, cards.Count + progress.Segments.Count);
                                },
                                ct);
                            context.WIP.ExecuteLocked(() => progress.Done = true);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            // Stopped: what was found so far is kept (and in the job state: the next run resumes from there)
                            cards.AddRange(ToTimeline(progress.Segments, timelineSegment.Offset));
                            break;
                        }
                    }

                    cards.AddRange(ToTimeline(progress.Segments, timelineSegment.Offset));
                    run.Report(timelineSegment.Offset + timelineSegment.Duration, cards.Count);
                }
                return cards;
            });
            return run;
        }

        private static IEnumerable<TitleCardSegment> ToTimeline(IEnumerable<TitleCardSegment> segments, TimeSpan offset)
            => segments.Select(s => new TitleCardSegment(offset + s.StartTime, offset + s.EndTime)).ToList();

        /// <summary>
        /// Waits for the detection (if it is not finished), with its progress, and adds a node for each title card.
        /// Q stops the detection: the title cards of what was not scanned yet are then missing.
        /// </summary>
        private void SpliceTitleCards(
            SubtitleGeneratorContext context,
            TimingRefiner refiner,
            TitleCardRun run)
        {
            if (run == null)
                return;

            if (!run.Task.IsCompleted)
            {
                context.WriteInfo("  Waiting for the title card detection to finish (PRESS Q TO STOP IT: the manual-input file will then have no title card for the part that was not scanned)...");

                var lastSave = DateTime.Now;
                while (!run.Task.IsCompleted)
                {
                    var percent = run.Total > TimeSpan.Zero ? 100.0 * run.Processed.Ticks / run.Total.Ticks : 0;
                    var stopText = run.StoppedByUser ? " Stopping..." : " (PRESS Q TO STOP)";
                    context.DefaultProgressUpdateHandler(
                        "TitleCardDetector",
                        null,
                        $"{percent:0}% scanned ({(int)run.Processed.TotalHours}:{run.Processed:mm\\:ss} of {(int)run.Total.TotalHours}:{run.Total:mm\\:ss}), {run.Found} title card(s) found.{stopText}");

                    if (!Console.IsInputRedirected && Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Q)
                    {
                        run.StoppedByUser = true;
                        run.Cancellation.Cancel();
                    }

                    if (DateTime.Now - lastSave > TimeSpan.FromSeconds(30))
                    {
                        context.WIP.Save(); // keeps what the detection found so far, in case the program stops
                        lastSave = DateTime.Now;
                    }
                    Task.WaitAny(new Task[] { run.Task }, TimeSpan.FromMilliseconds(250));
                }
                context.ClearProgressUpdate();
            }

            var cards = run.Task.GetAwaiter().GetResult(); // throws what went wrong in the detection
            refiner.SpliceTitleCards(cards.Select(c => (c.StartTime, c.EndTime)));
            context.WriteInfo(run.StoppedByUser
                ? $"  Title cards: {cards.Count} detected (stopped at {run.Processed:hh\\:mm\\:ss}: none after that)."
                : $"  Title cards: {cards.Count} detected.");
        }

        private void RefineAndExport(
            SubtitleGeneratorContext context,
            Transcription transcription,
            JobState jobState,
            TitleCardRun titleCardRun)
        {
            var inputs = LoadInputs(context);

            var refiner = CreateRefiner(context, transcription, null);
            var reports = new List<PhaseReport>();
            var splitReports = new List<PhaseReport>();
            int originalCount;
            int partCount = 0;

            if (this.RefineTiming.Enabled)
            {
                LoadCandidates(context, refiner, inputs);
                if (!RunPhases(context, transcription, refiner, jobState, reports, exportProgress: true))
                    return; // Interrupted

                refiner.FinalizeOverlaps();
                originalCount = refiner.Candidates.Count;
            }
            else
            {
                // Nothing is refined: the subtitles are kept as they are
                context.WriteInfo($"  RefineTiming is disabled: keeping the {inputs.Count} subtitles as they are.");
                foreach (var input in inputs)
                {
                    input.Item.Metadata.TryGetValue(this.Options.MetadataNeeded, out var originalText);
                    refiner.AddCandidateUnrefined(input.Item, originalText ?? "");
                }
                originalCount = refiner.Candidates.Count;
            }

            // The long subtitles are split, and only the parts are refined (their outer edges are already validated)
            // (a split part gets its text from the AI: not possible without it)
            var splits = this.Validation.Enabled && this.RefineTiming.UsesAI
                ? new TimingValidator(refiner, this.Validation).PlanSplits()
                : new List<TimingEdit>();
            if (splits.Count > 0)
            {
                var partInputs = BuildPartInputs(splits);
                partCount = partInputs.Count;
                context.WriteInfo($"  Fixes: {splits.Count} subtitle(s) split into {partCount} parts. Refining the parts...");

                var partsRefiner = CreateRefiner(context, transcription, refiner.TerrainMapping);
                LoadCandidates(context, partsRefiner, partInputs);
                if (!RunPhases(context, transcription, partsRefiner, jobState, splitReports, exportProgress: false))
                    return; // Interrupted

                refiner.ReplaceCandidates(splits.Select(s => s.Replaces), partsRefiner.Candidates);
                refiner.FinalizeOverlaps();
            }

            SpliceTitleCards(context, refiner, titleCardRun);
            if (this.Validation.Enabled)
                new TimingValidator(refiner, this.Validation).Run();

            if (this.RefineTiming.Enabled)
                refiner.AddFinalDipTraces();
            UpdateTranscription(transcription, refiner.Candidates);
            DoExportSrt(context, transcription.Id, refiner.Candidates.Where(c => !c.NeedsReview).Select(c => c.ToSubtitle()), "CLEAN");
            DoExportSrt(context, transcription.Id, refiner.Candidates.Where(c => c.NeedsReview).Select(c => c.ToReviewSubtitle()), "REVIEW");
            if (this.RefineTiming.Enabled)
                PrintSummary(context, transcription, refiner, reports, splitReports, originalCount, partCount);
            RunSimulations(context, transcription, refiner);
            SaveReport(context, transcription.Id);
            transcription.MarkAsFinished();
            context.WIP.Save();
        }

        /// <returns>false when the requests were interrupted (the caller must stop).</returns>
        private bool RunPhases(
            SubtitleGeneratorContext context,
            Transcription transcription,
            TimingRefiner refiner,
            JobState jobState,
            List<PhaseReport> reports,
            bool exportProgress)
        {
            if (refiner.SidesToAsk.Count == 0)
                context.WriteInfo("  RefineTiming is SnapOnly: timings snapped on the audio, no AI request.");

            foreach (var side in refiner.SidesToAsk)
            {
                if (!RunPhase(context, transcription, refiner, jobState, side, reports, exportProgress))
                    return false;
            }

            refiner.LockUnaskedBoundaries();
            return true;
        }

        /// <param name="exportProgress">false when the refiner only has some of the candidates (the parts of the split subtitles): the transcription is not updated with them.</param>
        private bool RunPhase(
            SubtitleGeneratorContext context,
            Transcription transcription,
            TimingRefiner refiner,
            JobState jobState,
            BoundarySide side,
            List<PhaseReport> reports,
            bool exportProgress)
        {
            var report = new PhaseReport { Side = side };
            reports.Add(report);

            var options = this.RefineTiming.InnerOptionsFor(side);
            refiner.BeginPhase(side);

            // Ends when PrepareNextRound has nothing left to ask (every boundary locked, or the runaway guard was reached)
            while (true)
            {
                refiner.PrepareNextRound(side);

                // PrepareNextRound leaves CurrentRound == null on locked / exhausted / auto-locked candidates
                var candidatesToTest = refiner.Candidates.Where(c => c.CurrentRound != null).ToList();

                report.Iterations++;
                context.WriteInfo($"  [{side}] Iteration {report.Iterations}: " +
                    $"{refiner.Candidates.Count(c => c[side].Locked)}/{refiner.Candidates.Count} locked, " +
                    $"testing {candidatesToTest.Count} candidates...");

                if (candidatesToTest.Count == 0)
                    break;

                var batch = BuildBatch(transcription, side, report.Iterations, candidatesToTest, jobState, options, report);
                if (!ExecuteBatch(context, transcription, jobState, batch, options))
                    return false; // Interrupted

                refiner.ProcessBatchResults(batch.Transcription.Items, side);
                if (exportProgress)
                {
                    UpdateTranscription(transcription, refiner.Candidates);
                    DoExportMetatadaSrt(context, transcription, false);
                }
            }

            // Whatever is still open on this side is locked and flagged, so the next pass can rely on it
            refiner.EndPhase(side);
            if (exportProgress)
            {
                UpdateTranscription(transcription, refiner.Candidates);
                DoExportMetatadaSrt(context, transcription, false);
            }
            context.WriteInfo($"  [{side}] Pass finished: {report.Tests} requests in {report.Iterations - 1} iterations.");
            return true;
        }

        private static JobState GetOrCreateJobState(Transcription transcription)
        {
            var jobState = transcription.CurrentJobState as JobState;
            if (jobState == null)
            {
                jobState = new JobState();
                transcription.CurrentJobState = jobState;
            }
            return jobState;
        }

        /// <param name="sharedTerrain">The terrain of the previous pass (null on the first pass: it is computed and exported).</param>
        private TimingRefiner CreateRefiner(SubtitleGeneratorContext context, Transcription transcription, AudioTerrainMapping sharedTerrain)
        {
            // Without refining there is no terrain (and no audio needed): the refiner is only the list of candidates
            var fullPcmAudioForRefiner = this.RefineTiming.Enabled
                ? context.WIP.AudioExtractions
                    .First(f => f.Id == this.RefineTiming.SourceAudioIdForTerrain && f.IsFinished)
                    .PcmAudio
                : null;

            var refiner = new TimingRefiner(fullPcmAudioForRefiner, this.RefineTiming, this.Validation, sharedTerrain);

            if (this.RefineTiming.Enabled && sharedTerrain == null)
            {
                var segments = refiner.TerrainMapping.Segments;
                context.WriteInfo($"  Preparation: Terrain mapping has {segments.Count(f => !f.IsSilence)} possible speech segments, {segments.Count(f => f.IsSilence)} silences.");
                DoExportSrt(context, transcription.Id, refiner.TerrainMapping.ToSegmentSubtitles(), "TERRAIN-MAP");
            }

            return refiner;
        }

        private List<RefineInput> LoadInputs(SubtitleGeneratorContext context)
        {
            var aggregation = this.Metadatas?.Aggregate(context);
            return (aggregation?.ReferenceTimingsWithMetadata ?? Array.Empty<TimedItemWithMetadata>())
                .OrderBy(f => f.StartTime)
                .Select(item => new RefineInput { Item = item, Origin = TimingOrigin.Original })
                .ToList();
        }

        private void LoadCandidates(SubtitleGeneratorContext context, TimingRefiner refiner, List<RefineInput> inputs)
        {
            context.WriteInfo($"  Preparation: Initializing acoustic bounds on {inputs.Count} candidates...");
            foreach (var input in inputs)
            {
                input.Item.Metadata.TryGetValue(this.Options.MetadataNeeded, out var originalText);
                refiner.AddCandidate(input.Item, originalText ?? "", input.Origin, input.FixedStart, input.FixedEnd);
            }
        }

        /// <summary>
        /// The new list of subtitles: the split ones are replaced by their parts. The parts have no text: the Start pass gives it.
        /// </summary>
        private List<RefineInput> BuildPartInputs(List<TimingEdit> splits)
        {
            var result = new List<RefineInput>();
            foreach (var split in splits)
            {
                for (int i = 0; i < split.Spans.Count; i++)
                {
                    var (start, end) = split.Spans[i];

                    var metadata = new MetadataCollection();
                    foreach (var kvp in split.Replaces.OriginalItem.Metadata)
                        metadata[kvp.Key] = kvp.Value;
                    metadata[this.Options.MetadataNeeded] = "";

                    result.Add(new RefineInput
                    {
                        Item = new TimedItemWithMetadata(start, end, metadata),
                        Origin = split.Origin,
                        // The outer edges of the subtitle were already validated: only the cuts between the parts are open
                        FixedStart = i == 0 ? start : (TimeSpan?)null,
                        FixedEnd = i == split.Spans.Count - 1 ? end : (TimeSpan?)null,
                    });
                }
            }
            return result.OrderBy(i => i.Item.StartTime).ToList();
        }

        // ---------------------------------------------------------------------
        // One iteration: build -> execute -> cache
        // ---------------------------------------------------------------------

        private RefineBatch BuildBatch(
            Transcription transcription,
            BoundarySide side,
            int iteration,
            List<TimingCandidate> candidatesToTest,
            JobState jobState,
            AIOptions options,
            PhaseReport report)
        {
            string defaultProduced = this.RefineTiming.Mode == RefineMode.SnapAndVerify ? TimingMeta.Verdict
                : side == BoundarySide.Start ? TimingMeta.BestStart : TimingMeta.BestEnd;
            string metadataProduced = string.IsNullOrWhiteSpace(options.MetadataAlwaysProduced)
                ? defaultProduced
                : options.MetadataAlwaysProduced;

            var batch = new RefineBatch
            {
                Transcription = new Transcription(
                    id: transcription.Id,
                    metadataAlwaysProduced: metadataProduced,
                    privateMetadataNames: null,
                    language: transcription.Language,
                    isFinished: false)
            };

            foreach (var candidate in candidatesToTest)
            {
                var round = candidate.CurrentRound;
                report.ClipDuration += round.Clips.Aggregate(TimeSpan.Zero, (sum, c) => sum + (c.End - c.Start));
                if (jobState.TryGetCachedResult(candidate.OriginalStartTime, round, out var cached))
                {
                    batch.Transcription.AddNewItem(candidate.OriginalStartTime, candidate.OriginalEndTime, cached.ToMetadata());
                    report.FromCache++;
                }
                else
                {
                    batch.ItemsToProcess.Add(new TimedItemWithMetadata(
                        candidate.OriginalStartTime,
                        candidate.OriginalEndTime,
                        BuildNodeMetadata(candidate, side)));
                    batch.ClipsByStartTime[candidate.OriginalStartTime] = round.Clips;
                    batch.NewlyRequested[candidate.OriginalStartTime] = candidate;
                }
                report.Tests++;
            }

            return batch;
        }

        /// <summary>Only what the pass needs: Start gets the previous bleed, End gets the next bleed.</summary>
        private static MetadataCollection BuildNodeMetadata(TimingCandidate candidate, BoundarySide side)
        {
            var nodeMeta = new MetadataCollection();
            nodeMeta[TimingMeta.OriginalText] = candidate.CurrentRound.TextSent ?? "";

            if (side == BoundarySide.Start && !string.IsNullOrWhiteSpace(candidate.PossibleBleedFromPrev))
                nodeMeta[TimingMeta.PossibleBleedFromPrev] = candidate.PossibleBleedFromPrev;

            if (side == BoundarySide.End && !string.IsNullOrWhiteSpace(candidate.PossibleBleedFromNext))
                nodeMeta[TimingMeta.PossibleBleedFromNext] = candidate.PossibleBleedFromNext;

            return nodeMeta;
        }

        /// <returns>false when the requests were interrupted (the caller must stop).</returns>
        private bool ExecuteBatch(
            SubtitleGeneratorContext context,
            Transcription transcription,
            JobState jobState,
            RefineBatch batch,
            AIOptions options)
        {
            m_activeClipsByStartTime = batch.ClipsByStartTime;
            try
            {
                if (batch.ItemsToProcess.Count > 0)
                {
                    var requestGenerator = ExecuteRequests(context, batch.Transcription, options, batch.ItemsToProcess.ToArray());
                    if (!requestGenerator.IsFinished())
                        return false;
                }
                return true;
            }
            finally
            {
                m_activeClipsByStartTime = null;
                transcription.Costs.AddRange(batch.Transcription.Costs);
                CacheNewResults(jobState, batch);
                context.WIP.Save();
            }
        }

        /// <summary>Caches only answers that really came from the AI (never re-caches cache hits, never caches empty answers).</summary>
        private static void CacheNewResults(JobState jobState, RefineBatch batch)
        {
            foreach (var item in batch.Transcription.Items)
            {
                if (!batch.NewlyRequested.TryGetValue(item.StartTime, out var candidate) || candidate.CurrentRound == null)
                    continue;

                var response = CachedCandidateResponse.FromMetadata(item.Metadata);
                if (!response.IsEmpty)
                    jobState.AddCachedResult(candidate.OriginalStartTime, candidate.CurrentRound, response);
            }
        }

        // ---------------------------------------------------------------------
        // Output
        // ---------------------------------------------------------------------

        private void UpdateTranscription(
            Transcription transcription,
            List<TimingCandidate> candidates)
        {
            var newItems = new List<TranscribedItem>();
            foreach (var candidate in candidates)
            {
                var meta = new MetadataCollection();
                foreach (var kvp in candidate.OriginalItem.Metadata)
                    meta[kvp.Key] = kvp.Value;

                // VoiceText (copied above) is the text of the first transcription ("" for a split part)
                meta[TimingMeta.VoiceTextValidated] = candidate.Origin == TimingOrigin.TitleCard ? "" : candidate.TextForAI ?? "";
                meta[this.Options.MetadataAlwaysProduced] = BuildOutputText(candidate);
                meta[TimingMeta.TimingTrace] = string.Join("\n", candidate.Traces);

                newItems.Add(new TranscribedItem(
                    candidate.Start.Current,
                    candidate.End.Current,
                    meta,
                    (candidate.OriginalItem as TranscribedItem)?.NoSpeechProbability ?? 0.0,
                    (candidate.OriginalItem as TranscribedItem)?.Words));
            }

            transcription.Items.Clear();
            transcription.Items.AddRange(newItems);
        }

        private const string GrabOnScreenTextMarker = "{GrabOnScreenText}";

        private string BuildOutputText(TimingCandidate candidate)
        {
            var sb = new StringBuilder();

            if (candidate.NeedsReview)
            {
                sb.AppendLine("[REVIEW]");
                if (!candidate.IsFullyLocked)
                    sb.AppendLine("Unfinished");
                foreach (var note in candidate.ValidationNotes)
                    sb.AppendLine("  " + note.Message);
            }

            if (this.IncludeTraceInVoiceTextForManualReviewAlways || (this.IncludeTraceInVoiceTextForManualReviewOnError && sb.Length > 0))
            {
                sb.AppendLine("[TRACES]");
                foreach (var trace in candidate.Traces)
                    sb.AppendLine($"  {trace}");
            }

            if (candidate.Origin == TimingOrigin.TitleCard)
            {
                // This metadata is what triggers the reading (OCR, translation...) of the title card
                if (sb.Length > 0)
                    sb.AppendLine();
                sb.AppendLine(GrabOnScreenTextMarker);
            }
            else if (this.IncludeTextInVoiceTextForManualReview)
            {
                if (sb.Length > 0)
                    sb.AppendLine();
                sb.AppendLine(candidate.FinalText);
            }

            return sb.ToString();
        }

        /// <summary>The lines of the final report (summary and simulations): shown, and saved in the report file when ExportMetadataSrt is on.</summary>
        private class ReportBuffer
        {
            public readonly List<string> Lines = new List<string>();

            public void Add(SubtitleGeneratorContext context, string message)
            {
                Lines.Add(message);
                context.WriteInfo(message);
            }
        }

        private readonly ReportBuffer m_report = new ReportBuffer();

        private void SaveReport(SubtitleGeneratorContext context, string transcriptionId)
        {
            if (!this.ExportMetadataSrt || m_report.Lines.Count == 0)
                return;

            var path = context.WIP.BaseFilePath + $".Worker.{transcriptionId}.report.txt";
            File.WriteAllLines(path, m_report.Lines, new UTF8Encoding(true));
            m_report.Lines.Clear();
        }

        private void DoExportSrt(
            SubtitleGeneratorContext context,
            string transcriptionId,
            IEnumerable<Subtitle> subtitles,
            string name)
        {
            if (this.ExportMetadataSrt)
            {
                var extension = $".Worker.{transcriptionId}.{name}.srt";
                var virtualSubtitleFile = context.WIP.CreateVirtualSubtitleFile();
                virtualSubtitleFile.Subtitles.AddRange(subtitles);
                virtualSubtitleFile.Save(context.WIP.ParentPath, extension, context.SoftDelete);
            }
        }

        private static string FormatDuration(TimeSpan t) => $"{(int)t.TotalHours}:{t:mm\\:ss\\.f}";

        /// <summary>The audio the AI had to listen to, compared with the audio of the subtitles and of the whole video.</summary>
        private void PrintClipDurations(
            SubtitleGeneratorContext context,
            TimingRefiner refiner,
            List<PhaseReport> reports,
            List<PhaseReport> splitReports)
        {
            var original = refiner.Candidates
                .Where(c => c.Origin != TimingOrigin.TitleCard)
                .Aggregate(TimeSpan.Zero, (sum, c) => sum + (c.OriginalEndTime - c.OriginalStartTime));
            var tested = reports.Concat(splitReports).Aggregate(TimeSpan.Zero, (sum, r) => sum + r.ClipDuration);
            var ratio = original > TimeSpan.Zero ? tested.TotalSeconds / original.TotalSeconds : 0;

            m_report.Add(context, $"  Audio clips tested: {FormatDuration(tested)} for {FormatDuration(original)} of original subtitles ({ratio:0.0}x), out of {FormatDuration(refiner.AudioEndTime)} of audio");
            foreach (var r in reports)
            {
                var split = splitReports.FirstOrDefault(f => f.Side == r.Side);
                var total = r.ClipDuration + (split?.ClipDuration ?? TimeSpan.Zero);
                m_report.Add(context, $"    {r.Side,-5} pass: {FormatDuration(total)}");
            }
        }

        private void PrintSummary(
            SubtitleGeneratorContext context,
            Transcription transcription,
            TimingRefiner refiner,
            List<PhaseReport> reports,
            List<PhaseReport> splitReports,
            int originalCount,
            int partCount)
        {
            var s = refiner.ComputeStats();
            if (s == null)
                return;

            const string line = "================================================================================";
            m_report.Add(context, line);
            m_report.Add(context, $"  TIMING REFINER SUMMARY ({transcription.Id})");
            m_report.Add(context, line);
            m_report.Add(context, partCount > 0
                ? $"  Total Candidates: {originalCount} (original), {s.Total} (after split)"
                : $"  Total Candidates: {s.Total}");
            if (s.TitleCards > 0)
                m_report.Add(context, $"  Title cards detected: {s.TitleCards} (not in the numbers below)");

            PrintClipDurations(context, refiner, reports, splitReports);

            // "Requests" = answers of the AI, including the ones that came from the cache
            foreach (var r in reports)
            {
                var text = $"  {r.Side,-5} pass requests: {r.Tests} ({((double)r.Tests / Math.Max(1, originalCount)):0.00} reqs/node";
                if (r.FromCache > 0)
                    text += $", {r.FromCache} from cache";
                text += ")";

                var split = splitReports.FirstOrDefault(f => f.Side == r.Side);
                if (split != null)
                    text += $" + {split.Tests} for the {partCount} split parts ({((double)split.Tests / Math.Max(1, partCount)):0.00} reqs/part)";

                m_report.Add(context, text);
            }
            m_report.Add(context, "");
            m_report.Add(context, "  QUALITY BREAKDOWN:");
            m_report.Add(context, $"    [OK]   Clean / Full Coverage:       {s.Clean,4}  ({Percent(s.Clean, s.Total):0.0}%)  Both sides locked, no warnings");
            m_report.Add(context, $"    [WARN] Review Needed:               {s.Review,4}  ({Percent(s.Review, s.Total):0.0}%)");

            foreach (var (key, label) in TimingRefinerStats.NoteCategories)
                PrintReviewLine(context, label, s.NoteCounts[key]);

            m_report.Add(context, "");
            m_report.Add(context, "  TIMING ADJUSTMENTS & ACCURACY:");
            m_report.Add(context, $"    - Avg Duration:                     {s.AvgOriginalDuration:0.00}s -> {s.AvgFinalDuration:0.00}s (diff: {(s.AvgFinalDuration - s.AvgOriginalDuration):+0.00;-0.00;0.00}s)");
            m_report.Add(context, $"    - Avg Start Drift:                  {s.AvgStartDrift:+0.00;-0.00;0.00}s (max outward: {s.MaxOutwardStartDrift:0.00}s)");
            m_report.Add(context, $"    - Avg End Drift:                    {s.AvgEndDrift:+0.00;-0.00;0.00}s (max outward: {s.MaxOutwardEndDrift:+0.00;-0.00;0.00}s)");
            m_report.Add(context, $"    - Overlaps / Squashed (<=0.05s):    {s.Overlaps} / {s.Squashed}");

            if (s.StartDipDeltas.Count > 0 || s.EndDipDeltas.Count > 0)
            {
                // (only the values that occurred; positive = outward: Start earlier / End later; the End is in reading order, as on the timeline)
                const int w = 6;
                var startKeys = s.StartDipDeltas.Keys.OrderByDescending(k => k).ToList();
                var endKeys = s.EndDipDeltas.Keys.OrderBy(k => k).ToList();
                string Cols(IEnumerable<int> keys) => string.Concat(keys.Select(k => k.ToString("+0;-0;0").PadLeft(w)));
                string Vals(IDictionary<int, int> d, IEnumerable<int> keys) => string.Concat(keys.Select(k => d[k].ToString().PadLeft(w)));

                m_report.Add(context, "");
                m_report.Add(context, "  DIPS MOVED (final vs initial snap, + = outward):");
                m_report.Add(context, $"    {"",-10}{"Start".PadRight(startKeys.Count * w)}    {"End"}");
                m_report.Add(context, $"    {"",-10}{Cols(startKeys)}    {Cols(endKeys)}");
                m_report.Add(context, $"    {"vs. Init",-10}{Vals(s.StartDipDeltas, startKeys)}    {Vals(s.EndDipDeltas, endKeys)}");
            }
            m_report.Add(context, line);
        }

        private static double Percent(int count, int total) => count * 100.0 / Math.Max(1, total);

        private void PrintReviewLine(SubtitleGeneratorContext context, string label, int count)
        {
            if (count > 0)
                m_report.Add(context, $"           - {(label + ":"),-33}{count,4}");
        }

        /// <summary>
        /// After refining, replays the refining with these settings, using this run's final timings as the truth (no AI call).
        /// The real settings are simulated first, as a calibration.
        /// </summary>
        public TimingSimulationVariant[] Simulations { get; set; }

        private void RunSimulations(SubtitleGeneratorContext context, Transcription transcription, TimingRefiner refiner)
        {
            // (the simulations replay the Full mode)
            if (this.Simulations == null || this.Simulations.Length == 0 || this.RefineTiming.Mode != RefineMode.Full)
                return;

            var pcm = context.WIP.AudioExtractions
                .First(f => f.Id == this.RefineTiming.SourceAudioIdForTerrain && f.IsFinished).PcmAudio;

            var truths = refiner.Candidates
                .Where(c => c.Origin == TimingOrigin.Original && c.OriginalItem != null)
                .Select(c => new TimingSimulator.Truth
                {
                    Item = c.OriginalItem,
                    Text = c.OriginalText,
                    Start = c.Start.Current,
                    End = c.End.Current,
                });

            var simulator = new TimingSimulator(pcm, refiner.TerrainMapping, this.RefineTiming.ValleyReach,
                this.Validation, transcription, truths);

            context.WriteInfo($"  Simulating {this.Simulations.Length} variant(s)...");
            var results = new List<TimingSimulator.Result> { simulator.Run("REAL settings (calibration)", this.RefineTiming) };
            foreach (var variant in this.Simulations)
                results.Add(simulator.Run(variant.Name ?? "(unnamed)", variant.Apply(this.RefineTiming)));

            TimingSimulator.Print(context, results, AIRequestPartAudio.AudioTokensPerSecond, message => m_report.Add(context, message));
        }
    }
}
