using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.Progress;
using FunscriptToolbox.SubtitlesVerbs.AudioExtractions;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscriberAI : Transcriber
    {
        [JsonProperty(Order = 30, Required = Required.Always)]
        public AIEngine Engine { get; set; }
        [JsonProperty(Order = 31)]
        public AIEngineConfigOverride EngineConfigOverride { get; set; }
        [JsonProperty(Order = 32, Required = Required.Always)]
        internal MetadataAggregator Metadatas { get; set; }
        [JsonProperty(Order = 33, Required = Required.Always)]
        public AIOptions Options { get; set; } = new AIOptions();
        [JsonProperty(Order = 34)]
        public bool PromptUserOnError { get; set; } = true;

        protected override string GetMetadataProduced() => this.Options.MetadataAlwaysProduced;

        protected override void DescribeDataFlow(WorkerDataFlow flow)
        {
            base.DescribeDataFlow(flow);
            flow.InputMetadatas(this.Metadatas);
            flow.InputExtractors(this.Options?.BinaryDataExtractors);
        }

        protected override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            foreach (var extractor in this.Options.BinaryDataExtractors?.OfType<BinaryDataExtractorAudio>() ?? Array.Empty<BinaryDataExtractorAudio>())
            {
                if (context.WIP.AudioExtractions.FirstOrDefault(f => f.Id == extractor.SourceAudioId && f.IsFinished) == null)
                {
                    reason = $"Audio extraction '{extractor.SourceAudioId}' is not done yet.";
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

        protected override void DoWorkInternal(SubtitleGeneratorContext context, Transcription transcription)
        {
            var metadataAggregation = this.Metadatas.Aggregate(context);
            var requestGenerator = ExecuteRequests(context, transcription, this.Options, metadataAggregation.ReferenceTimingsWithMetadata);
            if (requestGenerator.IsFinished())
            {
                transcription.MarkAsFinished();
                context.WIP.Save();
            }
        }

        protected virtual BinaryDataExtractorCachedCollection CreateBinaryDataExtractorCollection(
            BinaryDataExtractorExtended[] extractors)
        {
            return new BinaryDataExtractorCachedCollection(extractors);
        }

        protected AIRequestGenerator ExecuteRequests(
            SubtitleGeneratorContext context,
            Transcription transcription,
            AIOptions options,
            TimedItemWithMetadata[] itemsToBeProcessed)
        {
            var requestGenerator = new AIRequestGenerator(
                itemsToBeProcessed,
                transcription,
                transcription.Language,
                transcription.Language,
                options);

            var runner = new AIEngineRunner<TranscribedItem>(
                context,
                this.Engine,
                this.EngineConfigOverride,
                transcription,
                this.PromptUserOnError);

            var (allItems, itemsToDo, _, _) = requestGenerator.AnalyzeItemsState();
            allItems = allItems.OrderBy(f => f.StartTime).ToArray();

            var extractionLock = new object();

            // What the background preparation did (shown on its task)
            int backgroundGenerated = 0;
            int backgroundFromCache = 0;

            var binaryDataExtractors = CreateBinaryDataExtractorCollection(
                (options.BinaryDataExtractors ?? Array.Empty<BinaryDataExtractor>()).Select(extractor =>
                {
                    Func<AIRequestSection, ITiming, string, bool, AIRequestPart[]> getDataFunc;

                    if (extractor is BinaryDataExtractorAudio extractorAudio)
                    {
                        var fullPcmAudio = context.WIP.AudioExtractions.FirstOrDefault(f => f.Id == extractorAudio.SourceAudioId && f.IsFinished)?.PcmAudio;

                        getDataFunc = (section, timing, _, silent) =>
                        {
                            // (not silent = the request being prepared needs it now: the background didn't get to it yet)
                            if (!silent) context.DefaultProgressUpdateHandler("ffmpeg", $"{timing.StartTime}", "Audio clip not ready yet, waiting for the background preparation...");
                            // (when the background holds extractionLock, it gets the priority of the request while the request waits for it)
                            using var waitingForBackground = silent ? null : LockPriority.ForegroundWaitingForBackground();
                            lock (extractionLock)
                            {
                                var gapDuration = allItems.FirstOrDefault(f => f.StartTime > timing.StartTime)?.StartTime - timing.EndTime ?? TimeSpan.Zero;
                                var expandEndBy = (gapDuration < extractorAudio.FillGapSmallerThen)
                                    ? TimeSpanExtensions.Max(gapDuration, extractorAudio.ExpandEnd)
                                    : extractorAudio.ExpandEnd;
                                var startTime = timing.StartTime - extractorAudio.ExpandStart;
                                var endTime = timing.EndTime + expandEndBy;

                                var (_, filename, startTimeInRightFile) = context.WIP.TimelineMap.GetPathAndPosition(startTime);
                                var duration = fullPcmAudio != null
                                    ? (TimeSpanExtensions.Min(endTime, fullPcmAudio.EndTime) - TimeSpanExtensions.Max(startTime, TimeSpan.Zero))
                                    : (endTime - startTime);
                                var backupFilePath = context.GetPotentialVerboseFilePath($"{Path.GetFileNameWithoutExtension(filename)}_{extractorAudio.OutputFieldName}_{startTimeInRightFile:hh\\-mm\\-ss\\-fff}_{startTimeInRightFile + duration:hh\\-mm\\-ss\\-fff}.wav");

                                // 1. Read from cache if it already exists
                                if (File.Exists(backupFilePath))
                                {
                                    if (!silent) context.DefaultProgressUpdateHandler("ffmpeg", $"{timing.StartTime}", "Used cached audio.");
                                    else Interlocked.Increment(ref backgroundFromCache);
                                    var audioBytes = File.ReadAllBytes(backupFilePath);
                                    return new[]
                                    {
                                        new AIRequestPartAudio(
                                            section,
                                            $"{timing.StartTime:hh\\-mm\\-ss\\-fff}.wav",
                                            audioBytes,
                                            duration,
                                            timing.StartTime)
                                    };
                                }

                                // 2. Extract from PCM & generate .wav
                                if (!silent) context.DefaultProgressUpdateHandler("ffmpeg", $"{timing.StartTime}", $"Generating .wav for {timing.StartTime} to {timing.EndTime}");
                                else Interlocked.Increment(ref backgroundGenerated);

                                var snippet = fullPcmAudio.ExtractSnippet(startTime, endTime);
                                var audioBytesGenerated = snippet.ToWavBytes();

                                if (extractor.KeepTemporaryFiles)
                                    File.WriteAllBytes(backupFilePath, audioBytesGenerated);

                                return new[]
                                {
                                    new AIRequestPartAudio(
                                    section,
                                    $"{timing.StartTime:hh\\-mm\\-ss\\-fff}.wav",
                                    audioBytesGenerated,
                                    snippet.Duration,
                                    timing.StartTime)
                                };
                            }
                        };
                    }
                    else if (extractor is BinaryDataExtractorImage extractorImage)
                    {
                        getDataFunc = (section, timing, text, silent) =>
                        {
                            if (!silent) context.DefaultProgressUpdateHandler("ffmpeg", $"{timing.StartTime}", "Screenshot not ready yet, waiting for ffmpeg...");
                            // (when the background holds extractionLock, it gets the priority of the request while the request waits for it)
                            using var waitingForBackground = silent ? null : LockPriority.ForegroundWaitingForBackground();
                            lock (extractionLock)
                            {
                                var middleTime = TimeSpan.FromMilliseconds((timing.StartTime.TotalMilliseconds + timing.EndTime.TotalMilliseconds) / 2);
                                var (_, filename, middleTimeInRightFile) = context.WIP.TimelineMap.GetPathAndPosition(middleTime);

                                var backupFilePath = context.GetPotentialVerboseFilePath($"{Path.GetFileNameWithoutExtension(filename)}_{extractorImage.OutputFieldName}_{middleTimeInRightFile:hh\\-mm\\-ss\\-fff}.jpg");

                                // 1. Read from cache if it already exists
                                if (File.Exists(backupFilePath))
                                {
                                    if (!silent) context.DefaultProgressUpdateHandler("ffmpeg", $"{timing.StartTime}", "Used cached screenshot.");
                                    else Interlocked.Increment(ref backgroundFromCache);
                                    var image = File.ReadAllBytes(backupFilePath);
                                    return new[]
                                    {
                                        new AIRequestPartImage(
                                            section,
                                            $"{timing.StartTime:hh\\-mm\\-ss\\-fff}.jpg",
                                            image,
                                            timing.StartTime)
                                    };
                                }

                                // 2. Take screenshot via ffmpeg
                                if (!silent) context.DefaultProgressUpdateHandler("ffmpeg", $"{timing.StartTime}", $"Taking screenshot.");
                                else Interlocked.Increment(ref backgroundGenerated);
                                try
                                {
                                    var image = context.FfmpegHelper.TakeScreenshotAsBytes(
                                        Path.GetFullPath(Path.Combine(context.WIP.ParentPath, filename)),
                                        middleTimeInRightFile,
                                        ".jpg",
                                        extractorImage.FfmpegFilter?.Replace("[STARTTIME]", text == null ? string.Empty : context.FfmpegHelper.EscapeFfmpegDrawtext(text)));

                                    if (image == null)
                                    {
                                        context.WriteError($"Ffmpeg didn't extract a screenshot at {middleTimeInRightFile}.");
                                        return new AIRequestPartImage[0];
                                    }

                                    if (extractor.KeepTemporaryFiles)
                                        File.WriteAllBytes(backupFilePath, image);

                                    return new[]
                                    {
                                        new AIRequestPartImage(
                                            section,
                                            $"{timing.StartTime:hh\\-mm\\-ss\\-fff}.jpg",
                                            image,
                                            timing.StartTime)
                                    };
                                }
                                catch (Exception ex)
                                {
                                    context.WriteError($"Unable to extract screenshot at {middleTimeInRightFile}, ffmpeg returned errors: {ex.Message}");
                                    return new AIRequestPartImage[0];
                                }
                            }
                        };
                    }
                    else
                    {
                        throw new NotImplementedException();
                    }

                    var parts = new AIRequestPartCollection();
                    if (extractor.MetadataForTraining != null)
                    {
                        context.RunStep($"Prepare the training data ({extractor.OutputFieldName})", trainingNode =>
                        {
                            if (extractor.TextBeforeTrainingData != null)
                            {
                                parts.AddText(AIRequestSection.Training, extractor.TextBeforeTrainingData + "\n");
                            }

                            var trainingItems = allItems.Where(item => item.Metadata.ContainsKey(extractor.MetadataForTraining)).ToArray();
                            trainingNode.SetProgress(0, trainingItems.Length, "items");

                            var nbTrainingItems = 0;
                            foreach (var item in allItems)
                            {
                                if (item.Metadata.TryGetValue(extractor.MetadataForTraining, out var text))
                                {
                                    parts.AddText(AIRequestSection.Training, $"{text}\n");
                                    parts.AddRange(getDataFunc(AIRequestSection.Training, item, null, false));
                                    parts.AddText(AIRequestSection.Training, "\n---------------------\n");
                                    nbTrainingItems++;
                                    trainingNode.IncrementProgress();
                                }
                            }

                            if (extractor.TextAfterTrainingData != null)
                            {
                                parts.AddText(AIRequestSection.Training, "\n" + extractor.TextAfterTrainingData + "\n");
                            }

                            if (nbTrainingItems == 0)
                            {
                                parts.Clear();
                                trainingNode.SetState(TaskState.Skipped, "no training item");
                            }
                        });
                    }

                    return new BinaryDataExtractorExtended
                    {
                        Extractor = extractor,
                        TrainingContentLists = parts.ToArray(),
                        GetData = getDataFunc
                    };
                }).ToArray());

            // The audio clips / screenshots of the nodes to do are prepared in advance, in the background, while the requests are built and sent.
            // (its own task: started and disposed by the background code, so the 'current task' of the requests is not changed)
            var cts = new CancellationTokenSource();
            var extractorKinds = (options.BinaryDataExtractors ?? Array.Empty<BinaryDataExtractor>())
                .Where(e => e.Enabled)
                .Select(e => e is BinaryDataExtractorAudio ? "audio clips" : e is BinaryDataExtractorImage ? "screenshots" : e.OutputFieldName)
                .Distinct()
                .ToArray();
            var backgroundNode = (extractorKinds.Length > 0 && itemsToDo.Length > 0)
                ? context.PlanBackgroundStep($"Prepare the {string.Join(" and ", extractorKinds)} in advance")
                : null;
            var backgroundTask = backgroundNode == null ? Task.CompletedTask : Task.Run(() =>
            {
                // (lower priority on the shared ffmpeg locks than the requests being prepared, see LockPriority)
                LockPriority.MarkAsBackgroundWork();
                backgroundNode.Start();
                try
                {
                    var previousEndTime = TimeSpan.Zero;
                    var todoStartTimes = new System.Collections.Generic.HashSet<TimeSpan>(itemsToDo.Select(f => f.StartTime));
                    backgroundNode.SetProgress(0, todoStartTimes.Count, "nodes");
                    foreach (var itemTodo in allItems)
                    {
                        if (cts.IsCancellationRequested)
                        {
                            break;
                        }

                        var gapStartTime = previousEndTime;
                        previousEndTime = itemTodo.EndTime;
                        if (!todoStartTimes.Contains(itemTodo.StartTime))
                        {
                            continue;
                        }

                        // Same gap as AIRequestGenerator uses for the "context only" nodes.
                        binaryDataExtractors.GetContextOnlyNodes(
                            AIRequestSection.ContextNodes,
                            new Timing(gapStartTime, itemTodo.StartTime),
                            itemTodo.Metadata,
                            t => t.ToString(@"hh\:mm\:ss\.fff"),
                            silent: true).ToArray();

                        // Force extraction and caching of future nodes.
                        binaryDataExtractors.GetNamedContentListForItem(
                            AIRequestSection.PrimaryNodes,
                            itemTodo,
                            itemTodo.StartTime.ToString(@"hh\:mm\:ss\.fff"),
                            silent: true);

                        backgroundNode.IncrementProgress();
                        // (not counted when the extractors build their data themselves, ex. the padded clips of the manual-input preparer)
                        var generated = Volatile.Read(ref backgroundGenerated);
                        var fromCache = Volatile.Read(ref backgroundFromCache);
                        if (generated + fromCache > 0)
                            backgroundNode.SetStatus($"{generated} generated, {fromCache} read from the temporary files");
                    }

                    if (cts.IsCancellationRequested)
                    {
                        backgroundNode.SetState(TaskState.Skipped, "stopped: the requests are finished (or were stopped)");
                    }
                }
                catch (OperationCanceledException)
                {
                    backgroundNode.SetState(TaskState.Skipped, "stopped: the requests are finished (or were stopped)");
                }
                catch (Exception ex)
                {
                    // Not fatal: what is missing is done when the request needs it
                    backgroundNode.SetState(TaskState.Failed, ex.Message);
                    context.WriteInfo($"Background extraction error: {ex.Message}", ConsoleColor.Yellow);
                }
                finally
                {
                    backgroundNode.Dispose();
                }
            }, cts.Token);

            try
            {
                runner.Run(requestGenerator, binaryDataExtractors);
            }
            finally
            {
                cts.Cancel();
                // The worker must not end while its background task is still running (it stops after the clip / screenshot in progress)
                try { backgroundTask.Wait(TimeSpan.FromSeconds(10)); } catch (AggregateException) { }
                cts.Dispose();
            }

            return requestGenerator;
        }
    }
}