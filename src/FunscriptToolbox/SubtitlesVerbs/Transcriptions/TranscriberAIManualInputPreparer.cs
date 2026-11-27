using FunscriptToolbox.Core;
using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.Progress;
using FunscriptToolbox.SubtitlesVerbs.AudioExtractions;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    /// <summary>
    /// Prepares the subtitles of a transcription (full-ai) for the manual-input file: the nodes that are clean, and the ones
    /// that need the user's attention ('[REVIEW:Code:Detail] message' lines). It does up to three things, each with its own settings and Enabled
    /// (all disabled: the result is a clone of the source):
    ///   - RefineTiming: snaps the boundaries of each subtitle on the terrain of the audio, then refines them with its engine
    ///     (RefineTiming.Engine: the passes of requests to the AI, see TimingRefineEngine).
    ///   - Validation: checks the refined subtitles, flags what needs a look and splits the long ones.
    ///   - TitleCardDetection: adds a node for each title card found in the videos.
    /// </summary>
    [ConfigDoc("Prepares the subtitles of a transcription (ex. 'full-ai') for the manual-input file: refines their timings (RefineTiming), adds the title cards (TitleCardDetection), " +
        "and flags what needs a look (Validation: '[REVIEW:Code:Detail] reason' lines, checked one by one in the Review editor). All three disabled: a clone of the source.")]
    public class TranscriberAIManualInputPreparer : TranscriberAI
    {
        // ---------------------------------------------------------------------
        // Cache
        // ---------------------------------------------------------------------

        /// <summary>The answer of a request, kept in the cache: the metadata that the pass reads (see TimingRefinePass.CachedMetadataNames).</summary>
        public class CachedCandidateResponse
        {
            public Dictionary<string, string> Values { get; set; } = new Dictionary<string, string>();

            [JsonIgnore]
            public bool IsEmpty => Values == null || Values.Count == 0;

            public static CachedCandidateResponse FromMetadata(MetadataCollection metadata, IEnumerable<string> names)
            {
                var response = new CachedCandidateResponse();
                foreach (var name in names.Where(n => !string.IsNullOrEmpty(n)).Distinct())
                {
                    if (metadata.TryGetValue(name, out var value) && value != null)
                        response.Values[name] = value;
                }
                return response;
            }

            public MetadataCollection ToMetadata()
            {
                var metadata = new MetadataCollection();
                foreach (var kvp in Values ?? new Dictionary<string, string>())
                    metadata[kvp.Key] = kvp.Value;
                return metadata;
            }
        }

        public class JobState
        {
            /// <summary>The answers of the AI, by key of the request (see TimingRefineEngine.BuildCacheKey).</summary>
            public Dictionary<string, CachedCandidateResponse> CachedResults = new Dictionary<string, CachedCandidateResponse>();

            /// <summary>By filename of the video.</summary>
            public Dictionary<string, TitleCardProgress> TitleCards = new Dictionary<string, TitleCardProgress>();
        }

        // ---------------------------------------------------------------------
        // Multi-clip extractor
        // ---------------------------------------------------------------------

        private class MultiClipBinaryDataExtractorCollection : BinaryDataExtractorCachedCollection
        {
            private readonly Dictionary<TimeSpan, List<TimingClipOption>> r_clipsByStartTime;
            private readonly Func<BinaryDataExtractorAudio, AIRequestSection, TimingClipOption, bool, AIRequestPart[]> r_buildPaddedClip;

            public MultiClipBinaryDataExtractorCollection(
                BinaryDataExtractorExtended[] extractors,
                Dictionary<TimeSpan, List<TimingClipOption>> clipsByStartTime,
                Func<BinaryDataExtractorAudio, AIRequestSection, TimingClipOption, bool, AIRequestPart[]> buildPaddedClip)
                : base(extractors)
            {
                r_clipsByStartTime = clipsByStartTime ?? new Dictionary<TimeSpan, List<TimingClipOption>>();
                r_buildPaddedClip = buildPaddedClip;
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
                        // A padded clip is built from pieces (silence before / after): not the plain [Start, End] of the extractor
                        if (clip.HasPadding && audioExtractor.Extractor is BinaryDataExtractorAudio paddedExtractor)
                        {
                            result[clip.FieldName] = r_buildPaddedClip(paddedExtractor, section, clip, silent);
                            continue;
                        }

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
        private SubtitleGeneratorContext m_activeContext;
        private readonly object m_paddedClipLock = new object();

        protected override BinaryDataExtractorCachedCollection CreateBinaryDataExtractorCollection(
            BinaryDataExtractorExtended[] extractors)
        {
            if (m_activeClipsByStartTime != null)
            {
                return new MultiClipBinaryDataExtractorCollection(extractors, m_activeClipsByStartTime, BuildPaddedClipAudio);
            }
            return base.CreateBinaryDataExtractorCollection(extractors);
        }

        /// <summary>
        /// The audio of a clip with padding: [silence before] + [Start, End] + [silence after], cut in the audio of the extractor
        /// (the pieces of silence were chosen by the refiner, see TimingRefiner.AddClipPadding) and joined in one .wav.
        /// </summary>
        private AIRequestPart[] BuildPaddedClipAudio(BinaryDataExtractorAudio extractor, AIRequestSection section, TimingClipOption clip, bool silent)
        {
            var context = m_activeContext ?? throw new InvalidOperationException("A padded clip was asked outside of a batch.");
            var pcm = context.WIP.AudioExtractions
                .FirstOrDefault(f => f.Id == extractor.SourceAudioId && f.IsFinished)?.PcmAudio
                ?? throw new InvalidOperationException($"Audio extraction '{extractor.SourceAudioId}' is not done yet.");

            // (when the background holds m_paddedClipLock, it gets the priority of the request while the request waits for it)
            using var waitingForBackground = silent ? null : LockPriority.ForegroundWaitingForBackground();
            lock (m_paddedClipLock)
            {
                if (!silent) context.DefaultProgressUpdateHandler("ffmpeg", $"{clip.Start}", clip.Inserts.Count > 0
                    ? $"Generating the .wav for {clip.Start} to {clip.End} with {clip.Inserts.Count} spoken markers"
                    : $"Generating padded .wav for {clip.Start} to {clip.End} (+{clip.TotalDuration - (clip.End - clip.Start):ss\\.fff}s of silence)");

                // [silence before] + [Start, End] (with the spoken markers inserted, if any) + [silence after]
                var joined = SpokenMarkers.BuildClipAudio(pcm, clip);

                var bytes = joined.ToWavBytes();

                // To listen to what the AI hears (the silence spliced in front / after included).
                // Named <video>_<field>_padded_<original start of the subtitle>_<start of the clip>_<option>, so the files of a
                // subtitle are sorted together (by its original start), then by the position of each option.
                if (extractor.KeepTemporaryFiles)
                {
                    var (_, filename, startInFile) = context.WIP.TimelineMap.GetPathAndPosition(clip.Start);
                    var owner = m_activeClipsByStartTime?.FirstOrDefault(kv => kv.Value.Contains(clip));
                    var originalStart = owner?.Value != null ? owner.Value.Key : clip.Start;
                    var (_, _, originalStartInFile) = context.WIP.TimelineMap.GetPathAndPosition(originalStart);
                    File.WriteAllBytes(
                        context.GetPotentialVerboseFilePath($"{Path.GetFileNameWithoutExtension(filename)}_{extractor.OutputFieldName}_padded_{originalStartInFile:hh\\-mm\\-ss\\-fff}_{startInFile:hh\\-mm\\-ss\\-fff}_{clip.OptionId}.wav"),
                        bytes);
                }

                return new AIRequestPart[]
                {
                    new AIRequestPartAudio(section, $"{clip.Start:hh\\-mm\\-ss\\-fff}.wav", bytes, joined.Duration, clip.Start)
                };
            }
        }

        // ---------------------------------------------------------------------
        // Settings: the task does up to three things, each with its own settings and its own Enabled.
        // All three disabled: the output is a clone of the source (full-ai).
        // ---------------------------------------------------------------------

        [ConfigDoc("Adds the [TRACES] of the refining (each step, the options, the answers of the AI) to every subtitle of the manual-input file.",
            Advice = "Leave false (long subtitles to read); OnError already adds them where something was flagged.")]
        [JsonProperty(Order = 27)]
        public bool IncludeTraceInVoiceTextForManualReviewAlways { get; set; } = false;

        [ConfigDoc("Adds the [TRACES] of the refining to the subtitles that were flagged (or without a confidence): to understand why.")]
        [JsonProperty(Order = 28)]
        public bool IncludeTraceInVoiceTextForManualReviewOnError { get; set; } = true;

        [ConfigDoc("Adds the transcribed text to the subtitles of the manual-input file (false: only the timings and the review lines).")]
        [JsonProperty(Order = 29)]
        public bool IncludeTextInVoiceTextForManualReview { get; set; } = true;

        [ConfigDoc("Looks for title cards (text on a black background) in the videos and adds a subtitle for each (with {GrabOnScreenText}: its text is read later). null or not Enabled: not done.")]
        [JsonProperty(Order = 30)]
        public TitleCardDetectorOptions TitleCardDetection { get; set; }


        [ConfigDoc("Refines the Start and End of each subtitle: snapped on the terrain of the audio, then refined by the Engine (the AI, or nothing).")]
        [JsonProperty(Order = 31)]
        public TimingRefinerSettings RefineTiming { get; set; } = new TimingRefinerSettings();

        [ConfigDoc("Checks the refined subtitles, flags what needs a look (checked one by one in the Review editor) and fixes some problems. Needs RefineTiming. " +
            "(Also decides what the refining itself flags. Disabled: nothing is flagged at all.)")]
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
                    "Validation is enabled but RefineTiming is disabled: the validation needs the terrain of the audio (RefineTiming enabled, any engine). " +
                    "Enable RefineTiming or disable Validation.");
            }

            this.RefineTiming.Validate();
            this.Validation.Validate();
        }

        // ---------------------------------------------------------------------
        // Prerequisites
        // ---------------------------------------------------------------------

        protected override void DescribeDataFlow(WorkerDataFlow flow)
        {
            base.DescribeDataFlow(flow);
            if (this.RefineTiming?.Enabled == true)
            {
                flow.Input(DataKind.Audio, this.RefineTiming.SourceAudioIdForTerrain, "terrain");
                foreach (var inner in this.RefineTiming.Engine?.InnerOptionsUsed ?? Enumerable.Empty<AIOptions>())
                {
                    flow.InputExtractors(inner?.BinaryDataExtractors);
                }
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

                foreach (var inner in this.RefineTiming.Engine?.InnerOptionsUsed ?? Enumerable.Empty<AIOptions>())
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
            /// <summary>The pass that asks (its options, its engine: the cache key, what is kept from the answers).</summary>
            public TimingRefinePass Pass;
            public Transcription Transcription;
            public List<TimedItemWithMetadata> ItemsToProcess = new List<TimedItemWithMetadata>();
            public Dictionary<TimeSpan, List<TimingClipOption>> ClipsByStartTime = new Dictionary<TimeSpan, List<TimingClipOption>>();

            /// <summary>Candidates really sent to the AI (not answered from the cache), by OriginalStartTime.</summary>
            public Dictionary<TimeSpan, TimingCandidate> NewlyRequested = new Dictionary<TimeSpan, TimingCandidate>();
        }

        private class PhaseReport
        {
            public BoundarySide Side;

            /// <summary>The tail check of the chosen Ends (after the End pass), not a pass on its own side.</summary>
            public bool IsTailCheck;
            public string Label => IsTailCheck ? "Tail" : Side.ToString();
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
            Transcription transcription,
            JobState jobState,
            TitleCardRun titleCardRun)
        {
            var inputs = context.RunStep("Load the subtitles to refine", _ => LoadInputs(context));

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

                context.RunStep("Finalize the overlaps", _ => refiner.FinalizeOverlaps());
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
            if (this.Validation.Enabled && this.RefineTiming.Enabled && this.RefineTiming.Engine.CanSplitLongSubtitles)
            {
                var completed = context.RunStep("Split the long subtitles", splitNode =>
                {
                    var splits = new TimingValidator(refiner, this.Validation).PlanSplits();
                    if (splits.Count == 0)
                    {
                        splitNode.SetState(TaskState.Skipped, "no subtitle to split");
                        return true;
                    }

                    var partInputs = BuildPartInputs(splits);
                    partCount = partInputs.Count;
                    context.WriteInfo($"  Fixes: {splits.Count} subtitle(s) split into {partCount} parts. Refining the parts...");
                    splitNode.SetStatus($"{splits.Count} subtitle(s) split into {partCount} parts");

                    var partsRefiner = CreateRefiner(context, transcription, refiner.TerrainMapping);
                    LoadCandidates(context, partsRefiner, partInputs);
                    if (!RunPhases(context, transcription, partsRefiner, jobState, splitReports, exportProgress: false))
                    {
                        splitNode.SetState(TaskState.Cancelled, "interrupted");
                        return false; // Interrupted
                    }

                    refiner.ReplaceCandidates(splits.Select(s => s.Replaces), partsRefiner.Candidates);
                    refiner.FinalizeOverlaps();
                    return true;
                });
                if (!completed)
                    return; // Interrupted
            }

            if (titleCardRun != null)
                context.RunStep("Add the title cards", _ => SpliceTitleCards(context, refiner, titleCardRun));
            if (this.Validation.Enabled)
                context.RunStep("Validate the timings", _ => new TimingValidator(refiner, this.Validation).Run());

            context.RunStep("Export the subtitles (CLEAN / REVIEW)", _ =>
            {
                if (this.RefineTiming.Enabled)
                    refiner.AddFinalDipTraces();
                UpdateTranscription(transcription, refiner.Candidates);
                DoExportSrt(context, transcription.Id, refiner.Candidates.Where(c => !c.NeedsReview).Select(c => c.ToSubtitle()), "CLEAN");
                DoExportSrt(context, transcription.Id, refiner.Candidates.Where(c => c.NeedsReview).Select(c => c.ToReviewSubtitle()), "REVIEW");
            });
            if (this.RefineTiming.Enabled)
                context.RunStep("Summary", _ => PrintSummary(context, transcription, refiner, reports, splitReports, originalCount, partCount));
            if (this.Simulations != null && this.Simulations.Length > 0 && this.RefineTiming.Enabled && this.RefineTiming.Engine.SupportsSimulations)
                context.RunStep("Simulations", _ => RunSimulations(context, transcription, refiner));
            if (!string.IsNullOrWhiteSpace(this.GroundTruthExtension))
                context.RunStep("Ground truth analysis", _ => RunGroundTruthAnalysis(context, transcription, refiner));
            context.RunStep("Save the report", _ =>
            {
                SaveReport(context, transcription.Id);
                transcription.MarkAsFinished();
                context.WIP.Save();
            });
        }

        /// <summary>The passes of the engine (each one until it has nothing left to ask), then the sides not asked are locked.</summary>
        /// <returns>false when the requests were interrupted (the caller must stop).</returns>
        private bool RunPhases(
            SubtitleGeneratorContext context,
            Transcription transcription,
            TimingRefiner refiner,
            JobState jobState,
            List<PhaseReport> reports,
            bool exportProgress)
        {
            var passes = this.RefineTiming.Engine.CreatePasses(refiner);
            if (passes.Count == 0)
                context.WriteInfo($"  RefineTiming engine '{this.RefineTiming.Engine.GetType().Name}': timings snapped on the audio, no AI request.");

            foreach (var pass in passes)
            {
                var completed = context.RunStep(pass.IsTailCheck ? $"{pass.Name} check" : $"{pass.Name} pass", passNode =>
                {
                    if (RunPass(context, transcription, refiner, jobState, pass, reports, exportProgress))
                        return true;
                    passNode.SetState(TaskState.Cancelled, "interrupted");
                    return false;
                });
                if (!completed)
                    return false;
            }

            refiner.LockUnaskedBoundaries(passes);
            return true;
        }

        /// <param name="exportProgress">false when the refiner only has some of the candidates (the parts of the split subtitles): the transcription is not updated with them.</param>
        /// <returns>false when the requests were interrupted (the caller must stop).</returns>
        private bool RunPass(
            SubtitleGeneratorContext context,
            Transcription transcription,
            TimingRefiner refiner,
            JobState jobState,
            TimingRefinePass pass,
            List<PhaseReport> reports,
            bool exportProgress)
        {
            var report = new PhaseReport { Side = pass.Side, IsTailCheck = pass.IsTailCheck };
            reports.Add(report);

            pass.Begin();

            // Ends when the pass has nothing left to ask (every boundary locked, or the runaway guard was reached)
            while (true)
            {
                pass.PrepareNextRound();

                // (CurrentRound stays null on the candidates that have nothing to ask)
                var candidatesToTest = refiner.Candidates.Where(c => c.CurrentRound != null).ToList();

                if (pass.IsTailCheck)
                {
                    if (candidatesToTest.Count == 0)
                        break;
                    report.Iterations++;
                    context.WriteInfo($"  [{pass.Name}] Iteration {report.Iterations}: checking the audio appended by {candidatesToTest.Count} chosen Ends...");
                }
                else
                {
                    report.Iterations++;
                    context.WriteInfo($"  [{pass.Name}] Iteration {report.Iterations}: " +
                        $"{refiner.Candidates.Count(c => c[pass.Side].Locked)}/{refiner.Candidates.Count} locked, " +
                        $"testing {candidatesToTest.Count} candidates...");
                    if (candidatesToTest.Count == 0)
                        break;
                }

                var completed = context.RunStep($"Iteration {report.Iterations} ({candidatesToTest.Count} candidates)", iterationNode =>
                {
                    var batch = BuildBatchStep(context, transcription, pass, report.Iterations, candidatesToTest, jobState, report);
                    if (!ExecuteBatch(context, transcription, jobState, batch))
                    {
                        iterationNode.SetState(TaskState.Cancelled, "interrupted");
                        return false; // Interrupted
                    }

                    pass.ProcessResults(batch.Transcription.Items);
                    if (exportProgress && !pass.IsTailCheck)
                    {
                        UpdateTranscription(transcription, refiner.Candidates);
                        DoExportMetatadaSrt(context, transcription, false);
                    }
                    return true;
                });
                if (!completed)
                    return false; // Interrupted
            }

            // (ex. whatever is still open on this side is locked and flagged, so the next pass can rely on it)
            pass.End();
            if (exportProgress)
            {
                UpdateTranscription(transcription, refiner.Candidates);
                DoExportMetatadaSrt(context, transcription, false);
            }
            context.WriteInfo(pass.IsTailCheck
                ? $"  [{pass.Name}] Check finished: {report.Tests} requests in {Math.Max(0, report.Iterations)} iterations."
                : $"  [{pass.Name}] Pass finished: {report.Tests} requests in {report.Iterations - 1} iterations.");
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

            // (the terrain of the audio is mapped by the refiner, unless the one of the previous pass is given)
            var mapsTerrain = this.RefineTiming.Enabled && sharedTerrain == null;
            var refiner = context.RunStep(
                mapsTerrain ? "Map the audio terrain" : "Prepare the refiner",
                _ => new TimingRefiner(fullPcmAudioForRefiner, this.RefineTiming, this.Validation, sharedTerrain));

            if (mapsTerrain)
            {
                context.RunStep("Export the TERRAIN-MAP", _ =>
                {
                    var segments = refiner.TerrainMapping.Segments;
                    //context.WriteInfo($"  Preparation: Terrain mapping has {segments.Count(f => !f.IsSilence)} possible speech segments, {segments.Count(f => f.IsSilence)} silences " +
                    //    $"(second pass: {refiner.TerrainMapping.SplitMountains} mountains split at calm valleys, {refiner.TerrainMapping.ShoulderSplits} at a quiet shoulder, " +
                    //    $"{refiner.TerrainMapping.AddedMountains} mountains added in all; the pieces are marked '+' in the TERRAIN-MAP).");
                    DoExportSrt(context, transcription.Id, refiner.TerrainMapping.ToSegmentSubtitles(), "TERRAIN-MAP");
                });
            }

            return refiner;
        }

        /// <summary>
        /// Test only (to limit the tokens used): suffix of a subtitle file (ex: ".included-in-test.srt", one per video, loaded like the
        /// other subtitle files, in the time of the whole timeline). When set, only the subtitles (and title cards) that overlap
        /// a subtitle of that file are refined, validated, exported...: the others are left out as if they did not exist.
        /// The file must exist. Not set: everything is included.
        /// </summary>
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
            context.WriteInfo($"  Preparation: Initializing acoustic bounds on {inputs.Count} candidates...");
            context.RunStep("Load the candidates (acoustic bounds)", node =>
            {
                node.SetProgress(0, inputs.Count, "candidates");
                foreach (var input in inputs)
                {
                    input.Item.Metadata.TryGetValue(this.Options.MetadataNeeded, out var originalText);
                    refiner.AddCandidate(input.Item, originalText ?? "", input.Origin, input.FixedStart, input.FixedEnd);
                    node.IncrementProgress();
                }
            });
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

        /// <summary><see cref="BuildBatch"/>, as a step: what is asked, and what comes from the cache of the previous runs.</summary>
        private RefineBatch BuildBatchStep(
            SubtitleGeneratorContext context,
            Transcription transcription,
            TimingRefinePass pass,
            int iteration,
            List<TimingCandidate> candidatesToTest,
            JobState jobState,
            PhaseReport report)
        {
            return context.RunStep("Build the batch", node =>
            {
                var fromCacheBefore = report.FromCache;
                var batch = BuildBatch(transcription, pass, iteration, candidatesToTest, jobState, report);
                node.SetStatus($"{batch.ItemsToProcess.Count} to ask the AI, {report.FromCache - fromCacheBefore} from the cache");
                return batch;
            });
        }

        private RefineBatch BuildBatch(
            Transcription transcription,
            TimingRefinePass pass,
            int iteration,
            List<TimingCandidate> candidatesToTest,
            JobState jobState,
            PhaseReport report)
        {
            var side = pass.Side;
            var options = pass.Options;
            string metadataProduced = string.IsNullOrWhiteSpace(options.MetadataAlwaysProduced)
                ? pass.MetadataProduced
                : options.MetadataAlwaysProduced;

            var batch = new RefineBatch
            {
                Pass = pass,
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
                report.ClipDuration += round.Clips.Aggregate(TimeSpan.Zero, (sum, c) => sum + c.TotalDuration);
                if (jobState.CachedResults.TryGetValue(pass.Engine.BuildCacheKey(candidate, round), out var cached))
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
            RefineBatch batch)
        {
            var options = batch.Pass.Options;
            m_activeClipsByStartTime = batch.ClipsByStartTime;
            m_activeContext = context;
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
                m_activeContext = null;
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

                var response = CachedCandidateResponse.FromMetadata(item.Metadata, batch.Pass.CachedMetadataNames);
                if (!response.IsEmpty)
                    jobState.CachedResults[batch.Pass.Engine.BuildCacheKey(candidate, candidate.CurrentRound)] = response;
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
                if (!candidate.IsFullyLocked && !candidate.ValidationNotes.Any(n => n.Code == ValidationCode.Unfinished))
                    sb.AppendLine(new ValidationNote(ValidationCode.Unfinished, "The timing is not fully locked (start and/or end).").ToReviewLine());
                foreach (var note in candidate.ValidationNotes)
                    sb.AppendLine(note.ToReviewLine());
            }

            // The confidence of the engine in the timing (the 'Conf.' column of the Timing editor): not a finding, so not only when NeedsReview
            bool hasConfidence = candidate.TimingConfidence.HasValue;
            if (hasConfidence)
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "[TimingConfidence:{0:0.00}] {1}",
                    candidate.TimingConfidence.Value, (candidate.TimingConfidenceReason ?? "").Replace("\r", " ").Replace("\n", " ")).TrimEnd());

            if (this.IncludeTraceInVoiceTextForManualReviewAlways || (this.IncludeTraceInVoiceTextForManualReviewOnError && sb.Length > 0 && (candidate.NeedsReview || !hasConfidence)))
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
                var split = splitReports.FirstOrDefault(f => f.Label == r.Label);
                var total = r.ClipDuration + (split?.ClipDuration ?? TimeSpan.Zero);
                m_report.Add(context, $"    {r.Label,-5} pass: {FormatDuration(total)}");
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
                var text = $"  {r.Label,-5} pass requests: {r.Tests} ({((double)r.Tests / Math.Max(1, originalCount)):0.00} reqs/node";
                if (r.FromCache > 0)
                    text += $", {r.FromCache} from cache";
                text += ")";

                var split = splitReports.FirstOrDefault(f => f.Label == r.Label);
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
                // (only the values that occurred; positive = outward: Start earlier / End later)
                const int w = 6;
                var startKeys = s.StartDipDeltas.Keys.OrderByDescending(k => k).ToList();
                var endKeys = s.EndDipDeltas.Keys.OrderByDescending(k => k).ToList();
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

        /// <summary>
        /// Debug: suffix of the files (ex: ".manual-input.srt") that the user fixed by hand from the output of a previous run.
        /// When set, the final timings are compared with them (the truth) and the differences are exported
        /// (see <see cref="TimingGroundTruthAnalyzer"/>). Nothing is done when not set.
        /// </summary>
        public string GroundTruthExtension { get; set; }

        private void RunGroundTruthAnalysis(SubtitleGeneratorContext context, Transcription transcription, TimingRefiner refiner)
        {
            if (string.IsNullOrWhiteSpace(this.GroundTruthExtension))
                return;

            new TimingGroundTruthAnalyzer(context, transcription.Id, message => m_report.Add(context, message))
                .Run(refiner.Candidates, refiner.TerrainMapping, this.GroundTruthExtension);
        }

        private void RunSimulations(SubtitleGeneratorContext context, Transcription transcription, TimingRefiner refiner)
        {
            // (the simulations replay the engine: only the ones that support it, ex. StartEndJudge)
            if (this.Simulations == null || this.Simulations.Length == 0 || !this.RefineTiming.Enabled || !this.RefineTiming.Engine.SupportsSimulations)
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
