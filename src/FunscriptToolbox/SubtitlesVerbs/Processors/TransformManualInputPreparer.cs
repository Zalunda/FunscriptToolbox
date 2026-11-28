using FunscriptToolbox.Core;
using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.Progress;
using FunscriptToolbox.SubtitlesVerbs.Inputs;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using FunscriptToolbox.SubtitlesVerbs.Processors.Refiner;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FunscriptToolbox.SubtitlesVerbs.Processors
{
    /// <summary>
    /// Prepares the subtitles of a transcription for the manual-input file: the nodes that are clean, and the ones
    /// that need the user's attention ('[REVIEW:Code:Detail] message' lines). It does up to three things, each with its own settings and Enabled
    /// (all disabled: the result is a clone of the source):
    ///   - RefineTiming: snaps the boundaries of each subtitle on the terrain of the audio.
    ///   - Validation: checks the snapped subtitles and flags what needs a look.
    ///   - TitleCardDetection: adds a node for each title card found in the videos.
    /// </summary>
    [ConfigDoc("Prepares the subtitles of a transcription for the manual-input file: snaps their timings on the audio terrain (RefineTiming), adds the title cards (TitleCardDetection), " +
        "and flags what needs a look (Validation: '[REVIEW:Code:Detail] reason' lines, checked one by one in the Review editor). All three disabled: a clone of the source.")]
    public class TransformManualInputPreparer : Transform
    {
        public class JobState
        {
            /// <summary>By filename of the video.</summary>
            public Dictionary<string, TitleCardProgress> TitleCards = new Dictionary<string, TitleCardProgress>();
        }

        // ---------------------------------------------------------------------
        // Settings: the task does up to three things, each with its own settings and its own Enabled.
        // All three disabled: the output is a clone of the source.
        // ---------------------------------------------------------------------

        [ConfigDoc("The subtitles (TimingsSource) and the sources of their text (Sources).")]
        [JsonProperty(Order = 20, Required = Required.Always)]
        internal MetadataAggregator Metadatas { get; set; }

        [ConfigDoc("Only MetadataNeeded (the text, ex. \"VoiceText\") and MetadataAlwaysProduced (the result, ex. \"VoiceTextForManualReview\") are used: no AI.")]
        [JsonProperty(Order = 21, Required = Required.Always)]
        public AIOptions Options { get; set; } = new AIOptions();

        [ConfigDoc("Adds the [TRACES] of the snapping to every subtitle of the manual-input file.",
            Advice = "Leave false (long subtitles to read); OnError already adds them where something was flagged.")]
        [JsonProperty(Order = 27)]
        public bool IncludeTraceInVoiceTextForManualReviewAlways { get; set; } = false;

        [ConfigDoc("Adds the [TRACES] of the snapping to the subtitles that were flagged: to understand why.")]
        [JsonProperty(Order = 28)]
        public bool IncludeTraceInVoiceTextForManualReviewOnError { get; set; } = true;

        [ConfigDoc("Adds the transcribed text to the subtitles of the manual-input file (false: only the timings and the review lines).")]
        [JsonProperty(Order = 29)]
        public bool IncludeTextInVoiceTextForManualReview { get; set; } = true;

        [ConfigDoc("Looks for title cards (text on a black background) in the videos and adds a subtitle for each (with {GrabOnScreenText}: its text is read later). null or not Enabled: not done.")]
        [JsonProperty(Order = 30)]
        public TitleCardDetectorOptions TitleCardDetection { get; set; }

        [ConfigDoc("Snaps the Start and End of each subtitle on the terrain of the audio.")]
        [JsonProperty(Order = 31)]
        public TimingRefinerSettings RefineTiming { get; set; } = new TimingRefinerSettings();

        [ConfigDoc("Checks the snapped subtitles and flags what needs a look (checked one by one in the Review editor). Needs RefineTiming. " +
            "(Disabled: nothing is flagged at all.)")]
        [JsonProperty(Order = 32)]
        public TimingValidatorSettings Validation { get; set; } = new TimingValidatorSettings();

        private bool IsTitleCardDetectionEnabled => this.TitleCardDetection?.Enabled == true;

        /// <summary>A clear error when the settings can't work (a missing setting, or a combination that makes no sense).</summary>
        private void ValidateConfiguration()
        {
            if (this.RefineTiming == null)
                throw new InvalidOperationException("The 'RefineTiming' settings are missing (use {\"Enabled\": false} to not snap the timings).");
            if (this.Validation == null)
                throw new InvalidOperationException("The 'Validation' settings are missing (use {\"Enabled\": false} to not validate).");

            if (this.Validation.Enabled && !this.RefineTiming.Enabled)
            {
                throw new InvalidOperationException(
                    "Validation is enabled but RefineTiming is disabled: the validation needs the terrain of the audio (RefineTiming enabled). " +
                    "Enable RefineTiming or disable Validation.");
            }

            this.RefineTiming.Validate();
            this.Validation.Validate();
        }

        // ---------------------------------------------------------------------
        // Prerequisites
        // ---------------------------------------------------------------------

        protected override string GetMetadataProduced() => this.Options.MetadataAlwaysProduced;
        protected override string GetDefaultVerb() => "Preparing";

        protected override void DescribeDataFlow(WorkerDataFlow flow)
        {
            base.DescribeDataFlow(flow);
            flow.InputMetadatas(this.Metadatas);
            if (this.RefineTiming?.Enabled == true)
            {
                flow.Input(DataKind.Audio, this.RefineTiming.SourceAudioIdForTerrain, "terrain");
            }
        }

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
            }

            if (Metadatas?.Aggregate(context).IsPrerequisitesMetWithTimings(out reason) == false)
            {
                return false;
            }

            reason = null;
            return true;
        }

        // ---------------------------------------------------------------------
        // Main flow
        // ---------------------------------------------------------------------

        /// <summary>A subtitle given to the preparer, and where it comes from.</summary>
        private class RefineInput
        {
            public TimedItemWithMetadata Item;
            public TimingOrigin Origin;
        }

        protected override void DoWorkInternal(SubtitleGeneratorContext context, Track transcription)
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

            /// <summary>
            /// 'No more title cards' (a choice of the user): Done without being scanned after ProcessedUntil (considered to have no title card there).
            /// </summary>
            public bool SkippedByUser;

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

            /// <summary>
            /// True when the user stopped the detection ('Stop'): the worker stops, the detection continues from where it stopped at the next run.
            /// </summary>
            public volatile bool StoppedByUser;

            /// <summary>
            /// True when the user chose 'No more title cards': what is after Processed is considered to have no title card (also in the next runs).
            /// </summary>
            public volatile bool SkippedByUser;

            /// <summary>True while the detection waits for the lock 'VID' (the step that waits for the detection is shown as waiting too).</summary>
            public volatile bool IsWaitingForLock;

            /// <summary>How far the detection went in the whole timeline.</summary>
            public TimeSpan Processed => TimeSpan.FromTicks(Interlocked.Read(ref m_processedTicks));

            public int Found => Volatile.Read(ref m_found);

            /// <summary>The task of the detection (in the task tree / console progress line).</summary>
            public ITaskNode Node;

            private long m_scannedTicks;

            /// <summary>
            /// How far the scan is in the whole timeline (only to show the progress: it can be ahead of Processed,
            /// what is after Processed would be scanned again when resuming).
            /// </summary>
            public TimeSpan Scanned => TimeSpan.FromTicks(Math.Max(Interlocked.Read(ref m_scannedTicks), Interlocked.Read(ref m_processedTicks)));

            public void Report(TimeSpan processed, int found)
            {
                Interlocked.Exchange(ref m_processedTicks, processed.Ticks);
                Volatile.Write(ref m_found, found);
                ShowProgress();
                Node?.SetStatus($"{found} title card(s) found");
            }

            public void ReportScanned(TimeSpan scanned)
            {
                Interlocked.Exchange(ref m_scannedTicks, scanned.Ticks);
                ShowProgress();
            }

            private void ShowProgress()
            {
                if (Total > TimeSpan.Zero)
                {
                    Node?.SetProgress((long)Scanned.TotalSeconds, (long)Total.TotalSeconds, "%");
                }
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
            // Its own task, started and disposed by the background code (the 'current task' of the main work is not changed)
            var backgroundNode = context.PlanBackgroundStep("Title card detection");
            run.Node = backgroundNode;
            run.Task = Task.Run(async () =>
            {
                // (lower priority on the shared ffmpeg locks than the requests being prepared, see LockPriority)
                LockPriority.MarkAsBackgroundWork();
                backgroundNode.Start();
                try
                {
                    var foundCards = await DetectTitleCardsAsync();
                    if (ct.IsCancellationRequested)
                        backgroundNode.SetState(TaskState.Skipped,
                            run.StoppedByUser ? "stopped by the user (it continues at the next run)"
                            : run.SkippedByUser ? "'No more title cards' (the rest is not scanned)"
                            : "stopped");
                    return foundCards;
                }
                catch (Exception ex)
                {
                    backgroundNode.Fail(ex.Message);
                    throw;
                }
                finally
                {
                    backgroundNode.Dispose();
                }
            });
            return run;

            async Task<List<TitleCardSegment>> DetectTitleCardsAsync()
            {
                var cards = new List<TitleCardSegment>();
                // The lock 'VID' (reading the original video: limited like the other ffmpeg work on the video, when several videos are
                // processed in parallel): taken for the first part to scan, kept until the last one. Once a scene has started its
                // detection, it finishes all its parts before another scene can start (the scenes don't take the lock from each other
                // between two parts, and none is left half done, waiting).
                IDisposable videoLock = null;
                // 'No more title cards' (a choice of the user): the parts after it are done without being scanned
                var isRestSkipped = false;
                try
                {
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

                        if (isRestSkipped && !progress.Done)
                        {
                            context.WIP.ExecuteLocked(() =>
                            {
                                progress.Done = true;
                                progress.SkippedByUser = true;
                            });
                        }

                        if (!progress.Done)
                        {
                            try
                            {
                                if (videoLock == null)
                                {
                                    var nbParts = timelineSegments.Count();
                                    run.IsWaitingForLock = true;
                                    try
                                    {
                                        // (the wait ends when the user stops / skips the detection meanwhile)
                                        videoLock = context.Locks.VideoSource.Acquire(nbParts > 1
                                            ? $"title card detection of the {nbParts} parts (ffmpeg)"
                                            : "title card detection (ffmpeg)",
                                            cancellationToken: ct);
                                    }
                                    finally
                                    {
                                        run.IsWaitingForLock = false;
                                    }
                                }

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
                                    ct,
                                    scannedUntil => run.ReportScanned(timelineSegment.Offset + scannedUntil));
                                context.WIP.ExecuteLocked(() => progress.Done = true);
                            }
                            catch (OperationCanceledException) when (ct.IsCancellationRequested && run.SkippedByUser && !run.StoppedByUser)
                            {
                                // 'No more title cards' (a choice of the user): what was found so far is kept, the rest of this part and
                                // the next parts are done without title cards (in the job state: not scanned again in the next runs)
                                context.WIP.ExecuteLocked(() =>
                                {
                                    progress.Done = true;
                                    progress.SkippedByUser = true;
                                });
                                isRestSkipped = true;
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
                }
                finally
                {
                    videoLock?.Dispose();
                }
                return cards;
            }
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
                context.WriteInfo("  Waiting for the title card detection to finish ('Stop': the worker stops, the detection continues at the next run; " +
                    "'No more title cards': the rest of the scene is considered to have no title card, and the work continues)...");

                var lastSave = DateTime.Now;
                // Two buttons: 'Stop' (the worker stops, nothing is decided: the detection continues where it stopped at the next run,
                // Retry or Rescan) and 'No more title cards' (a choice of the user: the rest is not scanned, now or in the next runs).
                TaskScope.Current.SetCancellable("Stop the title card detection: the worker stops, and the detection continues where it stopped at the next run (Retry or Rescan).");
                TaskScope.Current.SetSkippable(
                    "No more title cards",
                    "The title card detection stops here, as your choice: the title cards found so far are kept, the rest of the scene is " +
                    "considered to have none (it's not scanned again in the next runs), and the work continues.");
                // While the detection (in the background) waits for the lock 'VID', this step is shown as waiting too: nothing is done
                // for the scene meanwhile (the web UI shows the scene in 'waiting', not in 'running').
                var isShownWaiting = false;
                while (!run.Task.IsCompleted)
                {
                    if (run.IsWaitingForLock != isShownWaiting)
                    {
                        isShownWaiting = run.IsWaitingForLock;
                        if (isShownWaiting)
                            TaskScope.Current.SetState(TaskState.WaitingForLock,
                                $"waiting for the title card detection, which waits for '{context.Locks.VideoSource.Name}' (used by: {context.Locks.VideoSource.CurrentHolderDescription ?? "?"})");
                        else
                            TaskScope.Current.SetState(TaskState.Running);
                    }

                    var scanned = run.Scanned;
                    var percent = run.Total > TimeSpan.Zero ? 100.0 * scanned.Ticks / run.Total.Ticks : 0;
                    var stopText = run.StoppedByUser ? " Stopping..." : run.SkippedByUser ? " No more title cards: stopping..." : string.Empty;
                    context.DefaultProgressUpdateHandler(
                        "TitleCardDetector",
                        null,
                        $"{percent:0}% scanned ({(int)scanned.TotalHours}:{scanned:mm\\:ss} of {(int)run.Total.TotalHours}:{run.Total:mm\\:ss}), {run.Found} title card(s) found.{stopText}");

                    if (context.PollStopRequest())
                    {
                        run.StoppedByUser = true;
                        run.Cancellation.Cancel();
                    }
                    else if (TaskScope.Current.IsSkipRequested && !run.SkippedByUser && !run.StoppedByUser)
                    {
                        run.SkippedByUser = true;
                        run.Cancellation.Cancel();
                    }

                    if (DateTime.Now - lastSave > TimeSpan.FromSeconds(30))
                    {
                        context.WIP.Save(); // keeps what the detection found so far, in case the program stops
                        lastSave = DateTime.Now;
                    }
                    Task.WaitAny(new Task[] { run.Task }, TimeSpan.FromMilliseconds(250));
                }
                if (isShownWaiting)
                    TaskScope.Current.SetState(TaskState.Running);
                context.ClearProgressUpdate();
                TaskScope.Current.SetCancellable(null);
                TaskScope.Current.SetSkippable(null, null);
            }

            var cards = run.Task.GetAwaiter().GetResult() // throws what went wrong in the detection
                .Where(c => IsIncludedInTest(c.StartTime, c.EndTime)) // (test mode: the cards outside of the test are left out)
                .ToList();

            if (run.StoppedByUser)
            {
                // 'Stop': nothing is decided, the worker stops (the answers of the AI and what the detection did are kept: the next run
                // continues the detection where it stopped)
                context.WIP.Save();
                throw new UserStoppedWorkerException($"Title card detection stopped by the user at {run.Processed:hh\\:mm\\:ss}: it continues there at the next run.");
            }

            refiner.SpliceTitleCards(cards.Select(c => (c.StartTime, c.EndTime)));
            context.WriteInfo(run.SkippedByUser
                ? $"  Title cards: {cards.Count} detected ('No more title cards' at {run.Processed:hh\\:mm\\:ss}: none after that, as chosen by the user)."
                : $"  Title cards: {cards.Count} detected.");
        }

        private void RefineAndExport(
            SubtitleGeneratorContext context,
            Track transcription,
            JobState jobState,
            TitleCardRun titleCardRun)
        {
            var inputs = context.RunStep("Load the subtitles to prepare", _ => LoadInputs(context));

            var refiner = CreateRefiner(context, transcription);

            if (this.RefineTiming.Enabled)
            {
                LoadCandidates(context, refiner, inputs);
                context.RunStep("Finalize the overlaps", _ => refiner.FinalizeOverlaps());
            }
            else
            {
                // Nothing is snapped: the subtitles are kept as they are
                context.WriteInfo($"  RefineTiming is disabled: keeping the {inputs.Count} subtitles as they are.");
                foreach (var input in inputs)
                {
                    input.Item.Metadata.TryGetValue(this.Options.MetadataNeeded, out var originalText);
                    refiner.AddCandidateUnrefined(input.Item, originalText ?? "");
                }
            }

            if (titleCardRun != null)
                context.RunStep("Add the title cards", _ => SpliceTitleCards(context, refiner, titleCardRun));
            if (this.Validation.Enabled)
                context.RunStep("Validate the timings", _ => new TimingValidator(refiner, this.Validation).Run());

            context.RunStep("Export the subtitles (CLEAN / REVIEW)", _ =>
            {
                UpdateTranscription(transcription, refiner.Candidates);
                DoExportSrt(context, transcription.Id, refiner.Candidates.Where(c => !c.NeedsReview).Select(c => c.ToSubtitle()), "CLEAN");
                DoExportSrt(context, transcription.Id, refiner.Candidates.Where(c => c.NeedsReview).Select(c => c.ToReviewSubtitle()), "REVIEW");
            });
            if (this.RefineTiming.Enabled)
                context.RunStep("Summary", _ => PrintSummary(context, transcription, refiner));
            if (!string.IsNullOrWhiteSpace(this.GroundTruthExtension))
                context.RunStep("Ground truth analysis", _ => RunGroundTruthAnalysis(context, transcription, refiner));
            context.RunStep("Save the report", _ =>
            {
                SaveReport(context, transcription.Id);
                transcription.MarkAsFinished();
                context.WIP.Save();
            });
        }

        private static JobState GetOrCreateJobState(Track transcription)
        {
            var jobState = transcription.CurrentJobState as JobState;
            if (jobState == null)
            {
                jobState = new JobState();
                transcription.CurrentJobState = jobState;
            }
            return jobState;
        }

        private TimingRefiner CreateRefiner(SubtitleGeneratorContext context, Track transcription)
        {
            // Without snapping there is no terrain (and no audio needed): the refiner is only the list of candidates
            var fullPcmAudioForRefiner = this.RefineTiming.Enabled
                ? context.WIP.AudioExtractions
                    .First(f => f.Id == this.RefineTiming.SourceAudioIdForTerrain && f.IsFinished)
                    .PcmAudio
                : null;

            var refiner = context.RunStep(
                this.RefineTiming.Enabled ? "Map the audio terrain" : "Prepare the refiner",
                _ => new TimingRefiner(fullPcmAudioForRefiner, this.RefineTiming, this.Validation));

            if (this.RefineTiming.Enabled)
            {
                context.RunStep("Export the TERRAIN-MAP", _ =>
                {
                    DoExportSrt(context, transcription.Id, refiner.TerrainMapping.ToSegmentSubtitles(), "TERRAIN-MAP");
                });
            }

            return refiner;
        }

        [ConfigDoc("Test only (to use fewer tokens): the suffix of a subtitle file next to the video (ex. \".included-in-test.srt\"). Only the subtitles (and title cards) " +
            "that overlap one of its subtitles are refined, validated, written...: the others are left out, as if they did not exist. The file must exist. null: everything is included.")]
        public string IncludedInTestExtension { get; set; }

        /// <summary>The time ranges of the file of IncludedInTestExtension (null = everything is included).</summary>
        private List<(TimeSpan Start, TimeSpan End)> m_includedInTest;

        private bool IsIncludedInTest(TimeSpan start, TimeSpan end)
            => m_includedInTest == null || m_includedInTest.Any(r => start < r.End && end > r.Start);

        private List<RefineInput> LoadInputs(SubtitleGeneratorContext context)
        {
            var aggregation = this.Metadatas?.Aggregate(context);
            var inputs = (aggregation?.ReferenceTimingsWithMetadata ?? Array.Empty<TimedItemWithMetadata>())
                .OrderBy(f => f.StartTime)
                .Select(item => new RefineInput { Item = item, Origin = TimingOrigin.Original })
                .ToList();

            m_includedInTest = null;
            if (string.IsNullOrWhiteSpace(this.IncludedInTestExtension))
                return inputs;

            var testFile = context.WIP.LoadVirtualSubtitleFile(this.IncludedInTestExtension)
                ?? throw new InvalidOperationException(
                    $"IncludedInTestExtension is '{this.IncludedInTestExtension}' but no such subtitle file was found (create it, or remove the setting to include every subtitle).");
            m_includedInTest = testFile.Subtitles.Select(s => (s.StartTime, s.EndTime)).ToList();

            var kept = inputs.Where(i => IsIncludedInTest(i.Item.StartTime, i.Item.EndTime)).ToList();
            if (kept.Count == 0)
                throw new InvalidOperationException(
                    $"None of the {inputs.Count} subtitles overlaps a subtitle of '{this.IncludedInTestExtension}' ({m_includedInTest.Count} subtitles in that file): nothing would be refined.");

            context.WriteInfo($"  TEST MODE: only {kept.Count} of the {inputs.Count} subtitles are included (the ones overlapping the {m_includedInTest.Count} subtitles of '{this.IncludedInTestExtension}'); the others are ignored.");
            return kept;
        }

        private void LoadCandidates(SubtitleGeneratorContext context, TimingRefiner refiner, List<RefineInput> inputs)
        {
            context.WriteInfo($"  Preparation: Snapping acoustic bounds on {inputs.Count} candidates...");
            context.RunStep("Load the candidates (acoustic bounds)", node =>
            {
                node.SetProgress(0, inputs.Count, "candidates");
                foreach (var input in inputs)
                {
                    input.Item.Metadata.TryGetValue(this.Options.MetadataNeeded, out var originalText);
                    refiner.AddCandidate(input.Item, originalText ?? "", input.Origin);
                    node.IncrementProgress();
                }
            });
        }

        // ---------------------------------------------------------------------
        // Output
        // ---------------------------------------------------------------------

        private void UpdateTranscription(
            Track transcription,
            List<TimingCandidate> candidates)
        {
            var newItems = new List<TimedItemWithMetadata>();
            foreach (var candidate in candidates)
            {
                var meta = new MetadataCollection();
                foreach (var kvp in candidate.OriginalItem.Metadata)
                    meta[kvp.Key] = kvp.Value;

                meta[TimingMeta.VoiceTextValidated] = candidate.Origin == TimingOrigin.TitleCard ? "" : candidate.OriginalText ?? "";
                meta[this.Options.MetadataAlwaysProduced] = BuildOutputText(candidate);
                meta[TimingMeta.TimingTrace] = string.Join("\n", candidate.Traces);

                newItems.Add(new TimedItemWithMetadata(
                    candidate.Start.Current,
                    candidate.End.Current,
                    meta));
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
                // One line per finding: '[REVIEW:Code]' or '[REVIEW:Code:Detail]', then the message (the Timing editor groups them by code / detail)
                foreach (var note in candidate.ValidationNotes)
                    sb.AppendLine(note.ToReviewLine());
            }

            if (this.IncludeTraceInVoiceTextForManualReviewAlways || (this.IncludeTraceInVoiceTextForManualReviewOnError && candidate.NeedsReview))
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

        /// <summary>The lines of the final report (summary): shown, and saved in the report file when ExportMetadataSrt is on.</summary>
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

        private void PrintSummary(
            SubtitleGeneratorContext context,
            Track transcription,
            TimingRefiner refiner)
        {
            var s = refiner.ComputeStats();
            if (s == null)
                return;

            const string line = "================================================================================";
            m_report.Add(context, line);
            m_report.Add(context, $"  TIMING REFINER SUMMARY ({transcription.Id})");
            m_report.Add(context, line);
            m_report.Add(context, $"  Total Candidates: {s.Total}");
            if (s.TitleCards > 0)
                m_report.Add(context, $"  Title cards detected: {s.TitleCards} (not in the numbers below)");

            m_report.Add(context, "");
            m_report.Add(context, "  QUALITY BREAKDOWN:");
            m_report.Add(context, $"    [OK]   Clean / Full Coverage:       {s.Clean,4}  ({Percent(s.Clean, s.Total):0.0}%)  No warnings");
            m_report.Add(context, $"    [WARN] Review Needed:               {s.Review,4}  ({Percent(s.Review, s.Total):0.0}%)");

            foreach (var (key, label) in TimingRefinerStats.NoteCategories)
                PrintReviewLine(context, label, s.NoteCounts[key]);

            m_report.Add(context, "");
            m_report.Add(context, "  TIMING ADJUSTMENTS & ACCURACY:");
            m_report.Add(context, $"    - Avg Duration:                     {s.AvgOriginalDuration:0.00}s -> {s.AvgFinalDuration:0.00}s (diff: {(s.AvgFinalDuration - s.AvgOriginalDuration):+0.00;-0.00;0.00}s)");
            m_report.Add(context, $"    - Avg Start Drift:                  {s.AvgStartDrift:+0.00;-0.00;0.00}s (max outward: {s.MaxOutwardStartDrift:0.00}s)");
            m_report.Add(context, $"    - Avg End Drift:                    {s.AvgEndDrift:+0.00;-0.00;0.00}s (max outward: {s.MaxOutwardEndDrift:+0.00;-0.00;0.00}s)");
            m_report.Add(context, $"    - Overlaps / Squashed (<=0.05s):    {s.Overlaps} / {s.Squashed}");
            m_report.Add(context, line);
        }

        private static double Percent(int count, int total) => count * 100.0 / Math.Max(1, total);

        private void PrintReviewLine(SubtitleGeneratorContext context, string label, int count)
        {
            if (count > 0)
                m_report.Add(context, $"           - {(label + ":"),-33}{count,4}");
        }

        /// <summary>(see <see cref="TimingGroundTruthAnalyzer"/>)</summary>
        [ConfigDoc("Debug: the suffix of the files (ex. \".manual-input.srt\") that the user fixed by hand from the result of a previous run. " +
            "The final timings are compared with them (the truth), and the differences written in the report. null: not done.")]
        public string GroundTruthExtension { get; set; }

        private void RunGroundTruthAnalysis(SubtitleGeneratorContext context, Track transcription, TimingRefiner refiner)
        {
            if (string.IsNullOrWhiteSpace(this.GroundTruthExtension))
                return;

            new TimingGroundTruthAnalyzer(context, transcription.Id, message => m_report.Add(context, message))
                .Run(refiner.Candidates, refiner.TerrainMapping, this.GroundTruthExtension);
        }
    }
}
