using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscriberAI : Transcriber
    {
        [JsonProperty(Order = 20)]
        public TimeSpan ExpandStart { get; set; } = TimeSpan.Zero;
        [JsonProperty(Order = 21)]
        public TimeSpan ExpandEnd { get; set; } = TimeSpan.Zero;
        [JsonProperty(Order = 22)]
        public bool UpdateTimingsBeforeSaving { get; set; } = false;
        [JsonProperty(Order = 23)]
        public bool AddSpeechCadenceBeforeSaving { get; set; } = false;

        [JsonProperty(Order = 30, Required = Required.Always)]
        public AIEngine Engine { get; set; }
        [JsonProperty(Order = 31, Required = Required.Always)]
        internal MetadataAggregator Metadatas { get; set; }
        [JsonProperty(Order = 32, Required = Required.Always)]
        public AIOptions Options { get; set; } = new AIOptions();

        protected override string GetMetadataProduced() => this.Options.MetadataAlwaysProduced;

        protected override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            foreach (var extractor in this.Options.BinaryDataExtractors.OfType<BinaryDataExtractorAudio>())
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

        private static Regex r_leadingTrailingRegex = new Regex(@"^(\.\.(?<Leading>\d+|\?)\.\.)?(?<VoiceText>.*?)(\.\.(?<Trailing>\d+|\?)\.\.)?$", RegexOptions.Compiled);

        protected override void DoWorkInternal(SubtitleGeneratorContext context, Transcription transcription)
        {
            var processStartTime = DateTime.Now;

            var requestGenerator = this.Metadatas
                .Aggregate(context)
                .CreateRequestGenerator(transcription, this.Options, transcription.Language);
            var runner = new AIEngineRunner<TranscribedItem>(
                context,
                this.Engine,
                transcription);

            var (allItems, _, _, _) = requestGenerator.AnalyzeItemsState();
            allItems = allItems.OrderBy(f => f.StartTime).ToArray();

            var binaryDataGenerator = new BinaryDataExtractorCachedCollection(
                this.Options.BinaryDataExtractors.Select(extractor =>
                {
                    Func<ITiming, string, dynamic[]> getDataFunc;

                    if (extractor is BinaryDataExtractorAudio extractorAudio)
                    {
                        var fullPcmAudio = context.WIP.AudioExtractions.FirstOrDefault(f => f.Id == extractorAudio.SourceAudioId && f.IsFinished)?.PcmAudio;
                        getDataFunc = (timing, _) =>
                        {
                            context.DefaultProgressUpdateHandler("ffmpeg", $"{timing.StartTime}", $"Generating .wav for {timing.StartTime} to {timing.EndTime}");
                            var tempWavFile = Path.GetTempFileName() + ".wav";

                            var gapDuration = allItems.FirstOrDefault(f => f.StartTime > timing.StartTime)?.StartTime - timing.EndTime ?? TimeSpan.Zero;
                            var expandEndBy = (gapDuration < extractorAudio.FillGapSmallerThen)
                                ? TimeSpanExtensions.Max(gapDuration, this.ExpandEnd)
                                : this.ExpandEnd;
                            context.FfmpegHelper.ConvertPcmAudioToOtherFormat(
                                fullPcmAudio.ExtractSnippet(
                                    timing.StartTime - this.ExpandStart,
                                    timing.EndTime + expandEndBy), tempWavFile);

                            var audioBytes = File.ReadAllBytes(tempWavFile);
                            var base64Audio = Convert.ToBase64String(audioBytes);
                            File.Delete(tempWavFile);
                            var data = new[]
                                {
                                            new
                                            {
                                                type = "input_audio",
                                                input_audio = new
                                                {
                                                    data = base64Audio,
                                                    format = "wav"
                                                }
                                            }
                                        };
                            if (extractor.KeepTemporaryFiles)
                                context.CreateVerboseBinaryFile($"{transcription.Id}_{timing.StartTime:hh\\-mm\\-ss\\-fff}.wav", audioBytes, processStartTime);
                            return data;
                        };
                    }
                    else if (extractor is BinaryDataExtractorImage extractorImage)
                    {
                        getDataFunc = (timing, text) =>
                        {
                            var middleTime = TimeSpan.FromMilliseconds((timing.StartTime.TotalMilliseconds + timing.EndTime.TotalMilliseconds) / 2);
                            context.DefaultProgressUpdateHandler("ffmpeg", $"{timing.StartTime}", $"Taking screenshot.");
                            var (filename, middleTimeInRightFile) = context.WIP.TimelineMap.GetPathAndPosition(middleTime);
                            var image = context.FfmpegHelper.TakeScreenshotAsBytes(
                                Path.GetFullPath(Path.Combine(context.WIP.ParentPath, filename)),
                                middleTimeInRightFile,
                                ".jpg",
                                extractorImage.FfmpegFilter?.Replace("[STARTTIME]", text == null ? string.Empty : context.FfmpegHelper.EscapeFfmpegDrawtext(text)));
                            var data = new[]
                                {
                                                    new
                                                    {
                                                        type = "image_url",
                                                        image_url = new
                                                        {
                                                            url = $"data:image/jpeg;base64,{Convert.ToBase64String(image)}"
                                                        }
                                                    }
                                                };
                            if (extractor.KeepTemporaryFiles)
                                context.CreateVerboseBinaryFile($"{transcription.Id}_{timing.StartTime:hh\\-mm\\-ss\\-fff}.jpg", image, processStartTime);
                            return data;
                        };
                    }
                    else
                    {
                        throw new NotImplementedException();
                    }

                    var contentList = new List<dynamic>();
                    if (extractor.MetadataForTraining != null)
                    {
                        if (extractor.TextBeforeTrainingData != null)
                        {
                            contentList.Add(new
                            {
                                type = "text",
                                text = extractor.TextBeforeTrainingData + "\n"
                            });
                        }

                        var nbTrainingItems = 0;
                        foreach (var item in allItems)
                        {
                            if (item.Metadata.TryGetValue(extractor.MetadataForTraining, out var text))
                            {
                                contentList.Add(new
                                {
                                    type = "text",
                                    text = $"{text}\n"
                                });
                                contentList.AddRange(getDataFunc(item, null));
                                nbTrainingItems++;
                            }
                        }

                        if (extractor.TextAfterTrainingData != null)
                        {
                            contentList.Add(new
                            {
                                type = "text",
                                text = "\n" + extractor.TextAfterTrainingData + "\n"
                            });
                        }

                        if (nbTrainingItems == 0)
                        {
                            contentList.Clear();
                        }
                    }

                    return new BinaryDataExtractorExtended
                    {
                        OutputFieldName = extractor.OutputFieldName ?? $"{extractor.DataType}",
                        DataType = extractor.DataType,
                        TrainingContentLists = contentList.ToArray(),
                        GetData = getDataFunc
                    };
                }).ToArray());
            runner.Run(requestGenerator, binaryDataGenerator);

            if (requestGenerator.IsFinished())
            {
                if (this.UpdateTimingsBeforeSaving || this.AddSpeechCadenceBeforeSaving)
                {
                    TimeSpan previousItemDesiredEndTime = TimeSpan.Zero;
                    for(int index = 0; index < transcription.Items.Count; index++)
                    {
                        var item = transcription.Items[index];
                        var previousItem = index > 0 ? transcription.Items[index - 1] : null;

                        var match = r_leadingTrailingRegex.Match(item.Metadata.Get(this.Options.MetadataAlwaysProduced));
                        if (match.Success)
                        {
                            var leadingDelay = TimeSpan.Zero;
                            if (match.Groups["Leading"].Success)
                            {
                                var leading = match.Groups["Leading"].Value;
                                if (leading != "?")
                                {
                                    leadingDelay = TimeSpan.FromSeconds(int.Parse(leading) * 0.1);
                                }
                                else
                                {
                                    leadingDelay = this.ExpandStart;
                                    item.Metadata["StartTimeTooLate"] = "true";
                                }
                            }
                            var trailingDelay = TimeSpan.Zero;
                            if (match.Groups["Trailing"].Success)
                            {
                                var trailing = match.Groups["Trailing"].Value;
                                if (trailing != "?")
                                {
                                    trailingDelay = TimeSpan.FromSeconds(int.Parse(trailing) * 0.1);
                                }
                                else
                                {
                                    trailingDelay = this.ExpandEnd;
                                    item.Metadata["EndTimeTooEarly"] = "true";
                                }
                            }

                            if (this.UpdateTimingsBeforeSaving)
                            {
                                item.StartTime = item.StartTime - this.ExpandStart + leadingDelay;
                                if (previousItem != null)
                                    previousItem.EndTime = (item.StartTime < previousItemDesiredEndTime) ? item.StartTime : previousItemDesiredEndTime;
                                previousItemDesiredEndTime = item.EndTime + this.ExpandEnd - trailingDelay;
                            }
                            if (this.AddSpeechCadenceBeforeSaving)
                            {
                                item.Metadata[this.Options.MetadataAlwaysProduced] = match.Groups["VoiceText"].Value;
                            }
                        }
                    }

                    if (this.UpdateTimingsBeforeSaving && transcription.Items.Count > 0)
                        transcription.Items.Last().EndTime = previousItemDesiredEndTime;
                }

                transcription.MarkAsFinished();
                context.WIP.Save();
            }
        }
    }
}