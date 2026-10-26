using FunscriptToolbox.Core.Infra;
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

            var binaryDataExtractors = new BinaryDataExtractorCachedCollection(
                options.BinaryDataExtractors.Select(extractor =>
                {
                    Func<AIRequestSection, ITiming, string, bool, AIRequestPart[]> getDataFunc;

                    if (extractor is BinaryDataExtractorAudio extractorAudio)
                    {
                        var fullPcmAudio = context.WIP.AudioExtractions.FirstOrDefault(f => f.Id == extractorAudio.SourceAudioId && f.IsFinished)?.PcmAudio;

                        getDataFunc = (section, timing, _, silent) =>
                        {
                            lock (extractionLock)
                            {
                                var gapDuration = allItems.FirstOrDefault(f => f.StartTime > timing.StartTime)?.StartTime - timing.EndTime ?? TimeSpan.Zero;
                                var expandEndBy = (gapDuration < extractorAudio.FillGapSmallerThen)
                                    ? TimeSpanExtensions.Max(gapDuration, extractorAudio.ExpandEnd)
                                    : extractorAudio.ExpandEnd;
                                var startTime = timing.StartTime - extractorAudio.ExpandStart;
                                var endTime = timing.EndTime + expandEndBy;

                                var (_, filename, startTimeInRightFile) = context.WIP.TimelineMap.GetPathAndPosition(startTime);
                                var backupFilePath = context.GetPotentialVerboseFilePath($"{Path.GetFileNameWithoutExtension(filename)}_{extractorAudio.OutputFieldName}_{startTimeInRightFile:hh\\-mm\\-ss\\-fff}_{startTimeInRightFile + timing.Duration:hh\\-mm\\-ss\\-fff}.wav");

                                var duration = fullPcmAudio != null
                                    ? (TimeSpanExtensions.Min(endTime, fullPcmAudio.EndTime) - TimeSpanExtensions.Max(startTime, TimeSpan.Zero))
                                    : (endTime - startTime);

                                // 1. Read from cache if it already exists
                                if (File.Exists(backupFilePath))
                                {
                                    if (!silent) context.DefaultProgressUpdateHandler("ffmpeg", $"{timing.StartTime}", "Used cached audio.");
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

                                var tempWavFile = PathExtension.GetTempFileName(".wav");
                                var snippet = fullPcmAudio.ExtractSnippet(startTime, endTime);
                                context.FfmpegHelper.ConvertPcmAudioToOtherFormat(snippet, tempWavFile);

                                var audioBytesGenerated = File.ReadAllBytes(tempWavFile);
                                File.Delete(tempWavFile);

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
                            lock (extractionLock)
                            {
                                var middleTime = TimeSpan.FromMilliseconds((timing.StartTime.TotalMilliseconds + timing.EndTime.TotalMilliseconds) / 2);
                                var (_, filename, middleTimeInRightFile) = context.WIP.TimelineMap.GetPathAndPosition(middleTime);

                                var backupFilePath = context.GetPotentialVerboseFilePath($"{Path.GetFileNameWithoutExtension(filename)}_{extractorImage.OutputFieldName}_{middleTimeInRightFile:hh\\-mm\\-ss\\-fff}.jpg");

                                // 1. Read from cache if it already exists
                                if (File.Exists(backupFilePath))
                                {
                                    if (!silent) context.DefaultProgressUpdateHandler("ffmpeg", $"{timing.StartTime}", "Used cached screenshot.");
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
                        if (extractor.TextBeforeTrainingData != null)
                        {
                            parts.AddText(AIRequestSection.Training, extractor.TextBeforeTrainingData + "\n");
                        }

                        var nbTrainingItems = 0;
                        foreach (var item in allItems)
                        {
                            if (item.Metadata.TryGetValue(extractor.MetadataForTraining, out var text))
                            {
                                parts.AddText(AIRequestSection.Training, $"{text}\n");
                                parts.AddRange(getDataFunc(AIRequestSection.Training, item, null, false));
                                parts.AddText(AIRequestSection.Training, "\n---------------------\n");
                                nbTrainingItems++;
                            }
                        }

                        if (extractor.TextAfterTrainingData != null)
                        {
                            parts.AddText(AIRequestSection.Training, "\n" + extractor.TextAfterTrainingData + "\n");
                        }

                        if (nbTrainingItems == 0)
                        {
                            parts.Clear();
                        }
                    }

                    return new BinaryDataExtractorExtended
                    {
                        Extractor = extractor,
                        TrainingContentLists = parts.ToArray(),
                        GetData = getDataFunc
                    };
                }).ToArray());

            var cts = new CancellationTokenSource();
            var backgroundTask = Task.Run(() =>
            {
                try
                {
                    if (binaryDataExtractors != null)
                    {
                        var previousEndTime = TimeSpan.Zero;
                        var todoStartTimes = new System.Collections.Generic.HashSet<TimeSpan>(itemsToDo.Select(f => f.StartTime));
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
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    context.WriteInfo($"Background extraction error: {ex.Message}", ConsoleColor.Yellow);
                }
            }, cts.Token);

            try
            {
                runner.Run(requestGenerator, binaryDataExtractors);
            }
            finally
            {
                cts.Cancel();
            }

            return requestGenerator;
        }
    }
}