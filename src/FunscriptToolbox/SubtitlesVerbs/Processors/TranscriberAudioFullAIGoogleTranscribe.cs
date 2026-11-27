using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using FunscriptToolbox.SubtitlesVerbs.Progress;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbs.Processors
{
    /// <summary>
    /// A full transcription by a transcription model of Google (ex. 'gemini-3.5-transcribe', see AIEngineAPIGoogle.Transcribe):
    /// verbatim, with the start / end of each word. The audio is sent in chunks of MaxChunkDuration (like TranscriberAudioFullAI:
    /// what starts in the last BufferZone of a chunk is done again by the next one). The subtitles are built from the words: a new one
    /// after a pause (MaxGap), after the end of a sentence (。？！?!), when it would be too long (MaxDuration), or when
    /// the speaker changes (Diarization). Their end is moved EndPadding later (the word ends of the model are too early), but not
    /// after the start of the next one.
    /// The answers of the model (the text and the words of each chunk) are kept in the job state (the only place with the words): the subtitles are built again
    /// from them at each run, so, with 'CanBeUpdated', a change of these rules is applied without any new request.
    /// The language: the one of the transcription (SourceLanguage).
    /// </summary>
    public class TranscriberAudioFullAIGoogleTranscribe : TranscriberAudio
    {
        public class JobState
        {
            /// <summary>Unified cache of all transcribed chunks (primary chunks and hole retries), keyed by exact (StartTime, EndTime).</summary>
            public List<CachedChunk> Chunks { get; set; } = new List<CachedChunk>();

            /// <summary>
            /// The subtitles that start before this time were made by the previous type of this step (TranscriberAudioFullAI, the same id,
            /// stopped before the end): kept as they are (null: none).
            /// </summary>
            public TimeSpan? PreviousTypeEndTime { get; set; }
        }

        public class CachedChunk
        {
            public TimeSpan StartTime { get; set; }
            public TimeSpan EndTime { get; set; }
            public string Text { get; set; }
            /// <summary>Their times: in the chunk.</summary>
            public AIEngineAPIGoogle.TranscribedWordInfo[] Words { get; set; }
            /// <summary>Non-null when the model returned status 'incomplete' (cut answer).</summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string IncompleteReason { get; set; }
        }

        [JsonProperty(Order = 20, Required = Required.Always)]
        public string MetadataProduced { get; set; }

        [ConfigDoc("The duration of the audio sent in each request. At most 30 minutes with the times of the words (a limit of the model: the rest would be lost).")]
        [JsonProperty(Order = 21)]
        public TimeSpan MaxChunkDuration { get; set; } = TimeSpan.FromMinutes(25);

        [ConfigDoc("Overlap duration between consecutive chunks so speech near a chunk boundary is transcribed cleanly past the model's ~10-15 s cold-start zone and away from the previous chunk's hard end.")]
        [JsonProperty(Order = 22)]
        public TimeSpan BufferZone { get; set; } = TimeSpan.FromSeconds(20);

        [ConfigDoc("Automatically retries any gap (hole) between subtitles of at least this duration using a targeted request with BufferZone pre-roll. null: disabled.")]
        [JsonProperty(Order = 23)]
        public TimeSpan? RetryGapMinDuration { get; set; } = null;

        [ConfigDoc("Hard cap on total audio duration to transcribe (primary chunks + all hole retries combined) as a multiple of the video duration (e.g. 2.0 = at most 2x video length, must be >= 1.0).")]
        [JsonProperty(Order = 24)]
        public double MaxTotalAudioFactor { get; set; } = 2.0;

        [JsonProperty(Order = 25)]
        public bool KeepTemporaryFiles { get; set; } = false;

        [JsonProperty(Order = 30, Required = Required.Always)]
        public AIEngineAPIGoogle Engine { get; set; }

        [ConfigDoc("The model separates the speakers: a new subtitle when the speaker changes. Its numbers (\"spk:0\", \"spk:1\"...) can't identify the " +
            "characters (ex. women with similar voices: it alternates between 2 numbers for 4 people; not the same numbers from a chunk to the next).")]
        [JsonProperty(Order = 40)]
        public bool Diarization { get; set; } = false;

        [ConfigDoc("With Diarization: the number of the speaker of each subtitle in this metadata (null: not written).")]
        [JsonProperty(Order = 41)]
        public string SpeakerMetadata { get; set; }

        [ConfigDoc("The same line returned twice (two speakers saying it together, or a doubling of the model): one subtitle, from the first start to " +
            "the last end, with MultipleSpeakersMetadata = MultipleSpeakersValue when they are from 2 speakers (null: not written).")]
        [JsonProperty(Order = 42)]
        public string MultipleSpeakersMetadata { get; set; }

        [JsonProperty(Order = 43)]
        public string MultipleSpeakersValue { get; set; } = "Multiple";

        [ConfigDoc("Two subtitles with the same text whose starts are at most this far apart: the same line (see MultipleSpeakersMetadata).")]
        [JsonProperty(Order = 44)]
        public TimeSpan SameLineMaxStartDifference { get; set; } = TimeSpan.FromSeconds(1.5);

        [ConfigDoc("Splits into a new subtitle when the pause between two words exceeds this duration (even without sentence-ending punctuation). null: disabled (no split on gap alone).")]
        [JsonProperty(Order = 50)]
        public TimeSpan? MaxGap { get; set; } = TimeSpan.FromMilliseconds(600);

        [ConfigDoc("Splits after sentence-ending punctuation (。？！?!) only when the pause to the next word is at least this duration. 00:00:00: always split; null: never split on punctuation.")]
        [JsonProperty(Order = 51)]
        public TimeSpan? EndOfSentenceMinGap { get; set; } = TimeSpan.Zero;

        [ConfigDoc("Splits into a new subtitle when adding the next word would exceed this duration. null: disabled.")]
        [JsonProperty(Order = 52)]
        public TimeSpan? MaxDuration { get; set; } = TimeSpan.FromSeconds(6);

        [ConfigDoc("The end of each subtitle is moved this much later (the word ends of the model are too early), but not after the start of the next one.")]
        [JsonProperty(Order = 54)]
        public TimeSpan EndPadding { get; set; } = TimeSpan.FromMilliseconds(250);

        [ConfigDoc("When true, subtitles transcribed in a chunk's overlap/buffer zone are kept if the next chunk produced no overlapping subtitle there (recovers cold-start misses at the start of a chunk).")]
        [JsonProperty(Order = 55)]
        public bool RecoverMissedChunkOverlap { get; set; } = true;

        [JsonProperty(Order = 60)]
        public TranscriberAudioFullAI.TranscriptioToIgnorePattern[] TranscriptionToIgnorePatterns { get; set; }

        [ConfigDoc("Peels off leading or trailing words that match TranscriptionToIgnorePatterns when separated from the rest of the subtitle by at least this pause (using word timings). null: disabled.")]
        [JsonProperty(Order = 61)]
        public TimeSpan? TrimIgnoredWordsMinGap { get; set; } = TimeSpan.FromMilliseconds(500);

        private const string EndOfSentence = "。？！?!";

        /// <summary>Keep this much margin before the end of chunk N when handing off to chunk N+1 inside the overlap.</summary>
        private static readonly TimeSpan HandoffEndMargin = TimeSpan.FromSeconds(5);

        /// <summary>The audio sent after the end of a hole, in a hole retry (the audio before it: BufferZone).</summary>
        private static readonly TimeSpan HolePostRoll = TimeSpan.FromSeconds(5);

        /// <summary>Margin used when checking whether the next chunk already covers an overlap subtitle from the previous chunk.</summary>
        private static readonly TimeSpan OverlapRecoveryMargin = TimeSpan.FromMilliseconds(300);

        /// <summary>A word that lasts longer than this: a broken time (seen at the start of some chunks), ignored.</summary>
        private static readonly TimeSpan MaxWordDuration = TimeSpan.FromSeconds(5);

        protected override string GetMetadataProduced() => this.MetadataProduced;

        protected override void DoWorkInternal(SubtitleGeneratorContext context, Track transcription)
        {
            if (this.MaxChunkDuration > TimeSpan.FromMinutes(30))
            {
                throw new Exception($"'{this.Id}': 'MaxChunkDuration' is {this.MaxChunkDuration}: at most 30 minutes (with the times of the words, the model ignores the rest).");
            }
            if (this.BufferZone < TimeSpan.Zero || this.BufferZone >= this.MaxChunkDuration)
            {
                throw new Exception($"'{this.Id}': 'BufferZone' ({this.BufferZone}) must be >= 0 and smaller than 'MaxChunkDuration' ({this.MaxChunkDuration}).");
            }
            if (this.MaxTotalAudioFactor < 1.0)
            {
                throw new Exception($"'{this.Id}': 'MaxTotalAudioFactor' ({this.MaxTotalAudioFactor}) must be >= 1.0.");
            }

            var processStartTime = DateTime.Now;
            var fullPcmAudio = base.GetPcmAudio(context);
            var options = new AIEngineAPIGoogle.TranscribeOptions
            {
                LanguageCodes = GetLanguageCodes(transcription.Language),
                Diarization = this.Diarization
            };

            var jobState = transcription.CurrentJobState as JobState;
            if (jobState == null)
            {
                // (the step was a TranscriberAudioFullAI before, with the same id: a transcription it started continues where it stopped,
                //  its subtitles are kept)
                // (subtitles without a job state of the previous type, ex. a run of an older version: kept; finished: nothing to ask,
                //  otherwise: it continues after the last one)
                var previous = transcription.CurrentJobState as TranscriberAudioFullAI.JobState;
                jobState = new JobState
                {
                    PreviousTypeEndTime = transcription.Items.Count == 0 ? (TimeSpan?)null
                        : transcription.IsFinished ? fullPcmAudio.EndTime + TimeSpan.FromHours(1)
                        : previous?.NextStartTime ?? transcription.Items.Max(i => i.EndTime)
                };
                transcription.CurrentJobState = jobState;
            }
            var previousTypeItems = jobState.PreviousTypeEndTime is TimeSpan previousEnd
                ? transcription.Items.Where(i => i.StartTime < previousEnd).ToList()
                : new List<TimedItemWithMetadata>();

            var maxAudioBudget = TimeSpan.FromTicks((long)(fullPcmAudio.EndTime.Ticks * this.MaxTotalAudioFactor));
            var requestNumber = 1;
            var isStopAfterRequested = false;
            var primaryChunks = new List<CachedChunk>();
            var holeRetries = new List<(CachedChunk Chunk, TimeSpan HoleStart, TimeSpan HoleEnd)>();
            var cursor = jobState.PreviousTypeEndTime ?? TimeSpan.Zero;

            // Phase 1: Primary chunking pass (walks from start through audio end, reusing exact (StartTime, EndTime) cache hits from jobState.Chunks)
            while (cursor < fullPcmAudio.EndTime)
            {
                StopOrPauseIfAsked();

                var chunkStartTime = cursor;
                var chunkEndTime = chunkStartTime + this.MaxChunkDuration;
                var chunkTiming = new Timing(chunkStartTime, chunkEndTime < fullPcmAudio.EndTime ? chunkEndTime : fullPcmAudio.EndTime);
                if (!IsWithinBudget(chunkTiming, out var budgetMessage))
                {
                    throw new Exception($"'{this.Id}': {budgetMessage}.");
                }
                var chunkName = $"chunk #{primaryChunks.Count + 1}, {chunkTiming.StartTime:hh\\:mm\\:ss} to {chunkTiming.EndTime:hh\\:mm\\:ss} of {fullPcmAudio.EndTime:hh\\:mm\\:ss}";
                var (chunk, cost, fromCache) = GetOrTranscribeChunk(chunkTiming, chunkName, hole: null);
                primaryChunks.Add(chunk);

                // (diarization: the model can stop giving the speakers in the middle of a chunk (seen on a long chunk with a lot of speech))
                var nbWithoutSpeaker = this.Diarization ? (chunk.Words ?? Array.Empty<AIEngineAPIGoogle.TranscribedWordInfo>()).Count(w => w.Speaker == null) : 0;
                if (nbWithoutSpeaker > 0 && !fromCache)
                {
                    context.WriteInfo($"{chunkName}: {nbWithoutSpeaker} of {chunk.Words.Length} words without a speaker (diarization lost by the model; a shorter 'MaxChunkDuration' may help).", ConsoleColor.Yellow);
                }

                // Where the next chunk starts: what starts in the buffer zone at the end of this one is done again by the next one
                var chunkItems = BuildItems(context, chunk, logIgnored: false, out _);
                if (chunk.IncompleteReason != null)
                {
                    // The answer was cut (ex. too many output tokens): the next chunk starts at the last subtitle received (it may be cut too)
                    var last = chunkItems.OrderBy(i => i.StartTime).LastOrDefault();
                    if (last == null || last.StartTime <= chunkStartTime)
                    {
                        primaryChunks.Remove(chunk);
                        jobState.Chunks.Remove(chunk);
                        throw new Exception($"The transcription of {chunkName} was cut ({chunk.IncompleteReason}) before a subtitle could be kept: reduce 'MaxChunkDuration' (or raise the maximum of output tokens of the engine, 'RequestBodyExtension').");
                    }
                    cursor = last.StartTime;
                    if (!fromCache)
                        context.WriteInfo($"Cut answer: kept up to {last.StartTime:hh\\:mm\\:ss\\.fff}, the next chunk starts there.", ConsoleColor.Yellow);
                }
                else if (chunkEndTime >= fullPcmAudio.EndTime)
                {
                    cursor = fullPcmAudio.EndTime;
                }
                else
                {
                    var startOfBufferZone = chunkEndTime - BufferZone;
                    var itemsToKeep = chunkItems.Where(i => i.StartTime < startOfBufferZone).ToList();
                    var itemsToRedo = chunkItems.Where(i => i.StartTime >= startOfBufferZone).ToList();
                    var keptEnd = itemsToKeep.Count > 0 ? itemsToKeep.Max(i => i.EndTime) : chunkStartTime;
                    var nextStartTime = TimeSpanExtensions.Max(startOfBufferZone, keptEnd);
                    if (itemsToRedo.Count > 0)
                    {
                        nextStartTime = TimeSpanExtensions.Min(nextStartTime, itemsToRedo.Min(i => i.StartTime));
                    }
                    cursor = nextStartTime;
                    if (!fromCache)
                        context.WriteInfo($"Buffer zone: kept {itemsToKeep.Count}, will redo {itemsToRedo.Count} item(s), next chunk starts at {nextStartTime:hh\\:mm\\:ss\\.fff}.");
                }

                if (cost != null)
                {
                    cost.NbItemsInResponse = chunkItems.Count;
                }

                if (!fromCache)
                {
                    context.RunStep("Build the subtitles from the words", _ => RebuildItems(context, transcription, primaryChunks, holeRetries, cursor, previousTypeItems));
                    context.WriteInfo($"Current number of subtitles: {transcription.Items.Count}");
                    context.WIP.Save();
                }
            }

            // (also when chunks were received: the chunks taken from the job state after them must be in the subtitles too)
            context.RunStep("Build the subtitles from the words", _ => RebuildItems(context, transcription, primaryChunks, holeRetries, cursor, previousTypeItems));

            // Phase 2: Optional pass to retry long silent holes (blackouts) using targeted requests with BufferZone pre-roll,
            // repeating if an 'incomplete' response made forward progress, capped by MaxTotalAudioFactor.
            if (this.RetryGapMinDuration is TimeSpan retryGapMinDuration && retryGapMinDuration > TimeSpan.Zero)
            {
                var triedHoles = new List<(TimeSpan Start, TimeSpan End)>();
                var budgetReached = false;
                var anyHoleFromCache = false;
                List<(TimeSpan WinStart, TimeSpan WinEnd, TimeSpan HoleStart, TimeSpan HoleEnd)> holesToRetry;
                while (!budgetReached
                    && (holesToRetry = FindUnretriedHoleWindows(transcription.Items, fullPcmAudio.EndTime, retryGapMinDuration, this.BufferZone, HolePostRoll, this.MaxChunkDuration, triedHoles)).Count > 0)
                {
                    var uncachedCount = holesToRetry.Count(h => FindCachedChunk(jobState, h.WinStart, h.WinEnd) == null);
                    if (uncachedCount > 0)
                    {
                        context.WriteInfo($"Hole retry: {holesToRetry.Count} window(s) ({uncachedCount} new, {holesToRetry.Count - uncachedCount} cached) for gaps >= {retryGapMinDuration:hh\\:mm\\:ss}.");
                    }
                    for (var h = 0; h < holesToRetry.Count; h++)
                    {
                        StopOrPauseIfAsked();

                        var (winStart, winEnd, holeStart, holeEnd) = holesToRetry[h];
                        var holeTiming = new Timing(winStart, winEnd);
                        if (!IsWithinBudget(holeTiming, out var budgetMessage))
                        {
                            context.WriteInfo($"Hole retry stopped: {budgetMessage}.", ConsoleColor.Yellow);
                            budgetReached = true;
                            break;
                        }

                        var holeName = $"hole #{h + 1}/{holesToRetry.Count}, {holeStart:hh\\:mm\\:ss} to {holeEnd:hh\\:mm\\:ss} (audio {winStart:hh\\:mm\\:ss} to {winEnd:hh\\:mm\\:ss})";
                        var (holeChunk, cost, fromCache) = GetOrTranscribeChunk(holeTiming, holeName, (holeStart, holeEnd));

                        var holeItems = BuildItems(context, holeChunk, logIgnored: false, out _);
                        var effectiveHoleEnd = holeEnd;
                        if (holeChunk.IncompleteReason != null)
                        {
                            var lastInHole = holeItems
                                .Where(i => i.StartTime > holeStart && i.StartTime < holeEnd)
                                .OrderBy(i => i.StartTime)
                                .LastOrDefault();
                            if (lastInHole != null)
                            {
                                effectiveHoleEnd = lastInHole.StartTime;
                                if (!fromCache)
                                    context.WriteInfo($"Cut hole retry: kept up to {effectiveHoleEnd:hh\\:mm\\:ss\\.fff}, remaining tail will be retried.", ConsoleColor.Yellow);
                            }
                            else if (!fromCache)
                            {
                                context.WriteInfo($"Cut hole retry produced no subtitles inside the hole; skipping further retry for this window.", ConsoleColor.Yellow);
                            }
                        }

                        holeRetries.Add((holeChunk, holeStart, effectiveHoleEnd));
                        triedHoles.Add((holeStart, effectiveHoleEnd));

                        if (cost != null)
                        {
                            cost.NbItemsInResponse = holeItems.Count;
                        }

                        var beforeCount = transcription.Items.Count;
                        if (!fromCache)
                        {
                            context.RunStep("Build the subtitles from the words", _ => RebuildItems(context, transcription, primaryChunks, holeRetries, cursor, previousTypeItems));
                            context.WriteInfo($"Hole retry #{h + 1}/{holesToRetry.Count}: +{transcription.Items.Count - beforeCount} subtitle(s) (total: {transcription.Items.Count}).");
                            context.WIP.Save();
                        }
                        else
                        {
                            anyHoleFromCache = true;
                            RebuildItems(context, transcription, primaryChunks, holeRetries, cursor, previousTypeItems);
                        }
                    }
                }

                if (anyHoleFromCache)
                {
                    context.WriteInfo($"Hole retry (from cache): {holeRetries.Count} cached window(s) applied (total: {transcription.Items.Count} subtitles).");
                }
            }

            transcription.MarkAsFinished();
            context.WIP.Save();

            // (safe point: the previous chunk is saved)
            void StopOrPauseIfAsked()
            {
                if (isStopAfterRequested)
                {
                    throw new UserStoppedWorkerException("Stopped after the request, as asked by the user.");
                }
                context.PauseIfClosing();
            }

            // The audio transcribed in this run (each chunk counted once), with this one when it's not in the cache: within MaxTotalAudioFactor?
            bool IsWithinBudget(Timing timing, out string message)
            {
                var usedAudio = TimeSpan.FromTicks(primaryChunks.Concat(holeRetries.Select(h => h.Chunk)).Distinct()
                    .Sum(c => Math.Max(0, (c.EndTime - c.StartTime).Ticks)));
                message = $"total transcribed audio ({usedAudio + timing.Duration:hh\\:mm\\:ss}) would exceed 'MaxTotalAudioFactor' ({this.MaxTotalAudioFactor:0.##}x = {maxAudioBudget:hh\\:mm\\:ss})";
                return FindCachedChunk(jobState, timing.StartTime, timing.EndTime) != null || usedAudio + timing.Duration <= maxAudioBudget;
            }

            // The answer for this audio: from the job state, or a new request (a hole retry: hole = the hole in this audio)
            (CachedChunk Chunk, Cost Cost, bool FromCache) GetOrTranscribeChunk(Timing timing, string chunkName, (TimeSpan Start, TimeSpan End)? hole)
            {
                var cached = FindCachedChunk(jobState, timing.StartTime, timing.EndTime);
                if (cached != null)
                {
                    context.WriteInfo($"Cache hit for {chunkName} ({cached.Words?.Length ?? 0} words).");
                    return (cached, null, true);
                }

                var fileName = $"{(hole == null ? string.Empty : "hole_")}{timing.StartTime:hh\\-mm\\-ss\\-fff}.wav";
                var requestDescription = hole is (TimeSpan holeStart, TimeSpan holeEnd)
                    ? $"hole {holeStart:hh\\:mm\\:ss\\.fff} to {holeEnd:hh\\:mm\\:ss\\.fff} (audio {timing.StartTime:hh\\:mm\\:ss\\.fff} to {timing.EndTime:hh\\:mm\\:ss\\.fff})"
                    : $"{timing.StartTime:hh\\:mm\\:ss\\.fff} to {timing.EndTime:hh\\:mm\\:ss\\.fff} out of {context.WIP.TimelineMap.Duration:hh\\:mm\\:ss\\.fff}";
                var number = requestNumber++;
                var request = context.RunStep($"Prepare {chunkName}", _ =>
                {
                    var snippet = fullPcmAudio.ExtractSnippet(timing.StartTime, timing.EndTime);
                    var audioBytes = snippet.ToWavBytes();
                    if (this.KeepTemporaryFiles)
                        context.CreateVerboseBinaryFile($"{transcription.Id}_{fileName}", audioBytes, processStartTime);
                    var userParts = new AIRequestPartCollection();
                    userParts.Add(new AIRequestPartAudio(AIRequestSection.PrimaryNodes, fileName, audioBytes, snippet.Duration, timing.StartTime));
                    return new AIRequest(
                        processStartTime,
                        number,
                        this.Id,
                        null,
                        new AIRequestPartCollection(),
                        userParts,
                        this.MetadataProduced,
                        requestDescription,
                        timing.StartTime);
                });
                context.PauseIfClosing();

                AIEngineAPIGoogle.TranscribeResult result;
                using (var requestNode = context.BeginTask(hole == null ? "AI request" : "AI request (hole retry)", chunkName))
                {
                    requestNode.SetCancellable(AIEngineAPI.StopNowHint);
                    requestNode.SetPauseAfterSupported();
                    requestNode.SetStopNowWarning(AIEngineAPI.StopNowCostWarning);
                    try
                    {
                        result = this.Engine.Transcribe(context, request, options);
                        isStopAfterRequested = requestNode.IsStopAfterRequested;
                    }
                    catch (UserStoppedWorkerException)
                    {
                        requestNode.SetState(TaskState.Cancelled, "stopped by the user");
                        throw;
                    }
                    catch (Exception ex)
                    {
                        requestNode.Fail(ex.Message);
                        throw;
                    }
                }

                var chunk = new CachedChunk
                {
                    StartTime = timing.StartTime,
                    EndTime = timing.EndTime,
                    Text = result.Text,
                    Words = result.Words,
                    IncompleteReason = result.IncompleteReason
                };
                jobState.Chunks.Add(chunk);

                if (result.Cost != null)
                {
                    dynamic customInfos = result.Cost.CustomInfos ?? new ExpandoObject();
                    customInfos.ChunkStartTime = $"{timing.StartTime:hh\\:mm\\:ss\\.fff}";
                    customInfos.ChunkEndTime = $"{timing.EndTime:hh\\:mm\\:ss\\.fff}";
                    customInfos.Duration = $"{timing.Duration:hh\\:mm\\:ss\\.fff}";
                    if (hole != null)
                        customInfos.HoleRetry = true;
                    result.Cost.CustomInfos = customInfos;
                    transcription.Costs.Add(result.Cost);
                }

                return (chunk, result.Cost, false);
            }
        }

        private static CachedChunk FindCachedChunk(JobState jobState, TimeSpan startTime, TimeSpan endTime)
        {
            return jobState.Chunks.FirstOrDefault(c =>
                Math.Abs((c.StartTime - startTime).TotalMilliseconds) < 1.0 &&
                Math.Abs((c.EndTime - endTime).TotalMilliseconds) < 1.0);
        }

        private static List<(TimeSpan WinStart, TimeSpan WinEnd, TimeSpan HoleStart, TimeSpan HoleEnd)> FindUnretriedHoleWindows(
            List<TimedItemWithMetadata> items,
            TimeSpan audioEnd,
            TimeSpan minGap,
            TimeSpan preRoll,
            TimeSpan postRoll,
            TimeSpan maxChunkDuration,
            List<(TimeSpan Start, TimeSpan End)> triedHoles)
        {
            var rawHoles = new List<(TimeSpan Start, TimeSpan End)>();
            var prevEnd = TimeSpan.Zero;
            foreach (var item in items.OrderBy(i => i.StartTime))
            {
                if (item.StartTime - prevEnd >= minGap)
                {
                    rawHoles.Add((prevEnd, item.StartTime));
                }
                if (item.EndTime > prevEnd)
                    prevEnd = item.EndTime;
            }
            if (audioEnd - prevEnd >= minGap)
            {
                rawHoles.Add((prevEnd, audioEnd));
            }

            var margin = TimeSpan.FromSeconds(1);
            var unretried = rawHoles.Where(h => !triedHoles.Any(t =>
                t.Start <= h.Start + margin && t.End >= h.End - margin)).ToList();

            // Expand with pre-roll / post-roll and merge overlapping windows up to maxChunkDuration
            TimeSpan WindowStart(TimeSpan holeStart) => holeStart > preRoll ? holeStart - preRoll : TimeSpan.Zero;
            var merged = new List<(TimeSpan WinStart, TimeSpan WinEnd, TimeSpan HoleStart, TimeSpan HoleEnd)>();
            foreach (var (hs, he) in unretried)
            {
                var ws = WindowStart(hs);
                var we = he + postRoll < audioEnd ? he + postRoll : audioEnd;
                if (merged.Count > 0 && ws <= merged[^1].WinEnd && (TimeSpanExtensions.Max(merged[^1].WinEnd, we) - merged[^1].WinStart) <= maxChunkDuration)
                {
                    var prev = merged[^1];
                    merged[^1] = (prev.WinStart, TimeSpanExtensions.Max(prev.WinEnd, we), prev.HoleStart, TimeSpanExtensions.Max(prev.HoleEnd, he));
                    continue;
                }

                // If a single hole window exceeds maxChunkDuration, split it cleanly
                for (var curHoleStart = hs; curHoleStart < he;)
                {
                    var curWinStart = WindowStart(curHoleStart);
                    var maxWinEnd = curWinStart + maxChunkDuration;
                    if (we <= maxWinEnd)
                    {
                        merged.Add((curWinStart, we, curHoleStart, he));
                        break;
                    }
                    var curHoleEnd = maxWinEnd - postRoll;
                    if (curHoleEnd <= curHoleStart)
                        curHoleEnd = maxWinEnd;
                    merged.Add((curWinStart, maxWinEnd, curHoleStart, curHoleEnd));
                    curHoleStart = curHoleEnd;
                }
            }
            return merged;
        }

        /// <summary>The language of the transcription, as a BCP-47 code with its main region (ex. "ja" => "ja-JP"); none: detected by the model.</summary>
        private static string[] GetLanguageCodes(Language language)
        {
            if (language == null)
                return Array.Empty<string>();
            try
            {
                return new[] { CultureInfo.CreateSpecificCulture(language.ShortName).Name };
            }
            catch (CultureNotFoundException)
            {
                return new[] { language.ShortName };
            }
        }

        /// <summary>A subtitle of these items overlaps the item (with OverlapRecoveryMargin on both sides).</summary>
        private static bool IsCovered(TimedItemWithMetadata item, IEnumerable<TimedItemWithMetadata> items)
        {
            return items.Any(other =>
                other.EndTime + OverlapRecoveryMargin > item.StartTime &&
                other.StartTime - OverlapRecoveryMargin < item.EndTime);
        }

        /// <summary>
        /// The subtitles of the transcription, built again from all the chunks kept (with the rules of now): the ones of each chunk that
        /// start before the next chunk (plus any overlap items missed by the next chunk when RecoverMissedChunkOverlap is true),
        /// after the subtitles of the previous type of the step, if any.
        /// </summary>
        private void RebuildItems(
            SubtitleGeneratorContext context,
            Track transcription,
            List<CachedChunk> primaryChunks,
            List<(CachedChunk Chunk, TimeSpan HoleStart, TimeSpan HoleEnd)> holeRetries,
            TimeSpan nextStartTime,
            List<TimedItemWithMetadata> previousTypeItems)
        {
            var perChunk = new List<(List<TimedItemWithMetadata> Items, List<string> Speakers)>(primaryChunks.Count);
            for (var index = 0; index < primaryChunks.Count; index++)
            {
                var chunkItems = BuildItems(context, primaryChunks[index], logIgnored: true, out var speakers);
                perChunk.Add((chunkItems, speakers));
            }

            // Compute the handoff timestamp for each boundary between chunk[i] and chunk[i + 1]:
            // Hand off inside the overlap at Max(midpoint, chunk[i].EndTime - HandoffEndMargin) so chunk[i] is used
            // up to ~5s before its hard end, and chunk[i+1] takes over well past its ~10-15s cold-start zone.
            var handoffs = new TimeSpan[Math.Max(0, primaryChunks.Count - 1)];
            for (var index = 0; index < handoffs.Length; index++)
            {
                var chunk = primaryChunks[index];
                var nextStart = primaryChunks[index + 1].StartTime;
                var overlap = chunk.EndTime - nextStart;
                handoffs[index] = overlap > TimeSpan.Zero
                    ? TimeSpanExtensions.Max(nextStart + TimeSpan.FromTicks(overlap.Ticks / 2), chunk.EndTime - HandoffEndMargin)
                    : nextStart;
            }

            var built = new List<(TimedItemWithMetadata Item, string Speaker)>();
            var recoveredFromOverlap = 0;
            for (var index = 0; index < primaryChunks.Count; index++)
            {
                var chunk = primaryChunks[index];
                var hasPrevChunk = index > 0;
                var hasNextChunk = index + 1 < primaryChunks.Count;
                TimeSpan? prevLimit = hasPrevChunk ? handoffs[index - 1] : (TimeSpan?)null;
                // (the last chunk, not the end of the audio: its buffer zone is in the next chunk, not asked yet)
                TimeSpan? nextLimit = hasNextChunk
                    ? handoffs[index]
                    : nextStartTime < chunk.EndTime ? nextStartTime : (TimeSpan?)null;
                var (chunkItems, speakers) = perChunk[index];
                for (var k = 0; k < chunkItems.Count; k++)
                {
                    var item = chunkItems[k];
                    var beforePrevLimit = prevLimit != null && item.StartTime < prevLimit.Value;
                    var atOrAfterNextLimit = nextLimit != null && item.StartTime >= nextLimit.Value;
                    // (in the part of the other chunk: kept only when it has nothing there, with RecoverMissedChunkOverlap)
                    // TO DO: 'nothing there' can be a blackout of the other chunk, and the item recovered can be cut by the end of its
                    // chunk: see 'TO DO NEXT' (blackouts, smarter retrier) in the notes of AIEngineAPIGoogle.Transcribe.cs.
                    var otherChunkItems = atOrAfterNextLimit && hasNextChunk ? perChunk[index + 1].Items
                        : beforePrevLimit ? perChunk[index - 1].Items
                        : null;
                    if (!beforePrevLimit && !atOrAfterNextLimit)
                    {
                        built.Add((item, speakers[k]));
                    }
                    else if (this.RecoverMissedChunkOverlap && otherChunkItems != null && !IsCovered(item, otherChunkItems))
                    {
                        built.Add((item, speakers[k]));
                        recoveredFromOverlap++;
                    }
                }
            }
            if (recoveredFromOverlap > 0)
            {
                context.WriteInfo($"Chunk overlap recovery: kept {recoveredFromOverlap} subtitle(s) from previous chunk buffer zones missed by the next chunk.");
            }

            // Merge subtitles recovered from targeted hole-retry chunks (only inside each hole's window, never overlapping existing items)
            var recoveredFromHoles = 0;
            foreach (var (holeChunk, rawHoleStart, rawHoleEnd) in holeRetries)
            {
                var holeItems = BuildItems(context, holeChunk, logIgnored: true, out var holeSpeakers);
                for (var k = 0; k < holeItems.Count; k++)
                {
                    var item = holeItems[k];
                    if (item.StartTime >= rawHoleStart - OverlapRecoveryMargin
                        && item.StartTime <= rawHoleEnd + OverlapRecoveryMargin
                        && !IsCovered(item, built.Select(b => b.Item)))
                    {
                        built.Add((item, holeSpeakers[k]));
                        recoveredFromHoles++;
                    }
                }
            }
            if (recoveredFromHoles > 0)
            {
                context.WriteInfo($"Hole retry recovery: kept {recoveredFromHoles} subtitle(s) from {holeRetries.Count} hole retry chunk(s).");
            }

            // In the order of the time (with several speakers, the model gives the words speaker by speaker, not in the order of the time)
            built = built.OrderBy(b => b.Item.StartTime).ToList();

            // The same line twice (the same text, starts close): one subtitle; from 2 speakers: MultipleSpeakersMetadata
            var merged = new List<TimedItemWithMetadata>();
            var used = new bool[built.Count];
            static string Normalize(string text) => new string((text ?? "").Where(char.IsLetterOrDigit).ToArray());
            for (var i = 0; i < built.Count; i++)
            {
                if (used[i])
                    continue;
                var (item, speaker) = built[i];
                var text = Normalize(item.Metadata.Get(this.MetadataProduced));
                var start = item.StartTime;
                var end = item.EndTime;
                var isMultiple = false;
                for (var j = i + 1; j < built.Count && built[j].Item.StartTime - item.StartTime <= this.SameLineMaxStartDifference; j++)
                {
                    if (used[j] || text.Length == 0 || Normalize(built[j].Item.Metadata.Get(this.MetadataProduced)) != text)
                        continue;
                    used[j] = true;
                    end = TimeSpanExtensions.Max(end, built[j].Item.EndTime);
                    isMultiple |= built[j].Speaker != speaker;
                }
                if (end != item.EndTime || isMultiple)
                {
                    var metadata = new MetadataCollection(item.Metadata);
                    if (isMultiple && !string.IsNullOrWhiteSpace(this.MultipleSpeakersMetadata))
                        metadata[this.MultipleSpeakersMetadata] = this.MultipleSpeakersValue;
                    item = new TimedItemWithMetadata(start, end, metadata);
                }
                merged.Add(item);
            }

            var items = new List<TimedItemWithMetadata>(previousTypeItems);
            items.AddRange(merged);

            // The end: EndPadding later (not after the start of the next one; a subtitle of at least 100 ms). Not the subtitles of the previous type.
            for (var i = previousTypeItems.Count; i < items.Count; i++)
            {
                var item = items[i];
                var end = item.EndTime + this.EndPadding;
                if (i + 1 < items.Count && end > items[i + 1].StartTime)
                    end = TimeSpanExtensions.Max(item.EndTime, items[i + 1].StartTime);
                if (end - item.StartTime < TimeSpan.FromMilliseconds(100))
                    end = item.StartTime + TimeSpan.FromMilliseconds(100);
                if (end != item.EndTime)
                    items[i] = new TimedItemWithMetadata(item.StartTime, end, item.Metadata);
            }

            transcription.Items.Clear();
            transcription.Items.AddRange(items);
        }

        /// <summary>
        /// The words with impossible times (seen at the start of some chunks): the end before the start, longer than MaxWordDuration, or
        /// a start far after the words that follow it in the text (more than 30 s later than the earliest of the next 20 words).
        /// </summary>
        private static List<AIEngineAPIGoogle.TranscribedWordInfo> RemoveBrokenWords(AIEngineAPIGoogle.TranscribedWordInfo[] words, out int nbBroken)
        {
            var kept = new List<AIEngineAPIGoogle.TranscribedWordInfo>();
            for (var i = 0; i < words.Length; i++)
            {
                var word = words[i];
                if (word.EndTime < word.StartTime || word.EndTime - word.StartTime > MaxWordDuration)
                    continue;
                var nextStarts = words.Skip(i + 1).Take(20).Select(w => w.StartTime).ToArray();
                if (nextStarts.Length > 0 && word.StartTime - nextStarts.Min() > TimeSpan.FromSeconds(30))
                    continue;
                kept.Add(word);
            }
            nbBroken = words.Length - kept.Count;
            return kept;
        }

        /// <summary>The subtitles of a chunk, from its words (times: in the chunk => in the scene), without the end padding.</summary>
        private List<TimedItemWithMetadata> BuildItems(SubtitleGeneratorContext context, CachedChunk chunk, bool logIgnored, out List<string> speakers)
        {
            var textBytes = Encoding.UTF8.GetBytes(chunk.Text ?? string.Empty);
            var groups = new List<List<AIEngineAPIGoogle.TranscribedWordInfo>>();
            List<AIEngineAPIGoogle.TranscribedWordInfo> current = null;
            var words = RemoveBrokenWords(chunk.Words ?? Array.Empty<AIEngineAPIGoogle.TranscribedWordInfo>(), out var nbBroken);
            if (nbBroken > 0 && logIgnored)
                context.WriteLog($"Chunk {chunk.StartTime:hh\\:mm\\:ss}: {nbBroken} word(s) with impossible times ignored.");
            foreach (var word in words)
            {
                if (current != null)
                {
                    var previous = current[^1];
                    var gap = word.StartTime - previous.EndTime;
                    var gapTooLong = this.MaxGap.HasValue && gap > this.MaxGap.Value;
                    var tooLong = this.MaxDuration.HasValue && word.EndTime - current[0].StartTime > this.MaxDuration.Value;
                    var previousText = (previous.Text ?? "").Trim();
                    var endOfSentence = this.EndOfSentenceMinGap.HasValue
                        && previousText.Length > 0
                        && EndOfSentence.Contains(previousText[^1])
                        && gap >= this.EndOfSentenceMinGap.Value;
                    var otherSpeaker = this.Diarization && word.Speaker != previous.Speaker;
                    if (gapTooLong || endOfSentence || tooLong || otherSpeaker)
                        current = null;
                }
                if (current == null)
                {
                    current = new List<AIEngineAPIGoogle.TranscribedWordInfo>();
                    groups.Add(current);
                }
                current.Add(word);
            }

            var items = new List<TimedItemWithMetadata>();
            speakers = new List<string>();
            var trimmedEdges = 0;
            string ExtractRangeText(List<AIEngineAPIGoogle.TranscribedWordInfo> grp, int from, int to)
            {
                var f = grp[from];
                var l = grp[to];
                return f.StartIndex != null && l.EndIndex != null && l.EndIndex <= textBytes.Length && f.StartIndex < l.EndIndex
                    ? Encoding.UTF8.GetString(textBytes, f.StartIndex.Value, l.EndIndex.Value - f.StartIndex.Value).Trim()
                    : string.Concat(grp.Skip(from).Take(to - from + 1).Select(w => w.Text)).Trim();
            }

            // The words before the first pause >= TrimIgnoredWordsMinGap (leading) / after the last one (trailing), when they match
            // TranscriptionToIgnorePatterns (or have no text): removed from the group.
            bool TrimEdge(List<AIEngineAPIGoogle.TranscribedWordInfo> group, bool leading)
            {
                if (group.Count <= 1)
                    return false;
                var wordsAfterPause = Enumerable.Range(1, group.Count - 1)
                    .Where(i => group[i].StartTime - group[i - 1].EndTime >= this.TrimIgnoredWordsMinGap.Value)
                    .DefaultIfEmpty(-1);
                var split = leading ? wordsAfterPause.First() : wordsAfterPause.Last();
                if (split < 0)
                    return false;
                var (from, to) = leading ? (0, split - 1) : (split, group.Count - 1);
                var edgeText = ExtractRangeText(group, from, to);
                var edgePattern = edgeText.Length == 0 ? null : this.TranscriptionToIgnorePatterns.FirstOrDefault(p => p.Regex.IsMatch(edgeText));
                if (edgeText.Length > 0 && edgePattern == null)
                    return false;
                if (logIgnored && edgePattern != null)
                    context.WriteLog($"Trimming {(leading ? "leading" : "trailing")} '{edgePattern.Name}' at [{context.WIP.TimelineMap.ConvertToPartSpecificFileIndexAndTime(chunk.StartTime + group[from].StartTime)}]: {edgeText}");
                group.RemoveRange(from, to - from + 1);
                return true;
            }

            foreach (var group in groups)
            {
                // Peel off leading / trailing words matching TranscriptionToIgnorePatterns when separated by >= TrimIgnoredWordsMinGap
                if (this.TrimIgnoredWordsMinGap.HasValue && this.TranscriptionToIgnorePatterns != null && this.TranscriptionToIgnorePatterns.Length > 0)
                {
                    while (TrimEdge(group, leading: true))
                        trimmedEdges++;
                    while (TrimEdge(group, leading: false))
                        trimmedEdges++;
                }

                // (the text between the first and the last word, as in the text of the answer: with its spaces / punctuation)
                var first = group[0];
                var last = group[^1];
                var text = ExtractRangeText(group, 0, group.Count - 1);
                if (text.Length == 0)
                    continue;

                var startTime = chunk.StartTime + first.StartTime;
                var pattern = this.TranscriptionToIgnorePatterns?.FirstOrDefault(p => p.Regex.IsMatch(text));
                if (pattern != null)
                {
                    if (logIgnored)
                        context.WriteLog($"Ignoring '{pattern.Name}' transcription at [{context.WIP.TimelineMap.ConvertToPartSpecificFileIndexAndTime(startTime)}]: {text}");
                    continue;
                }

                var metadata = new MetadataCollection();
                metadata[this.MetadataProduced] = text;
                if (this.Diarization && first.Speaker != null && !string.IsNullOrWhiteSpace(this.SpeakerMetadata))
                    metadata[this.SpeakerMetadata] = first.Speaker;
                speakers.Add(first.Speaker);
                items.Add(new TimedItemWithMetadata(
                    startTime,
                    chunk.StartTime + last.EndTime,
                    metadata));
            }
            if (trimmedEdges > 0 && logIgnored)
                context.WriteLog($"Chunk {chunk.StartTime:hh\\:mm\\:ss}: trimmed {trimmedEdges} leading/trailing ignored word group(s).");
            return items;
        }
    }
}
