using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscriberAudioFullAI : TranscriberAudio
    {
        public class JobState
        {
            public TimeSpan NextStartTime { get; set; }
        }

        [JsonProperty(Order = 20, Required = Required.Always)]
        public string MetadataProduced { get; set; }

        [JsonProperty(Order = 21)]
        public TimeSpan MaxChunkDuration { get; set; } = TimeSpan.FromMinutes(15);

        [JsonProperty(Order = 22)]
        public bool KeepTemporaryFiles { get; set; } = false;

        [JsonProperty(Order = 30, Required = Required.Always)]
        public AIEngine Engine { get; set; }

        [JsonProperty(Order = 31)]
        public AIPrompt SystemPrompt { get; set; }

        [JsonProperty(Order = 32)]
        public AIPrompt UserPrompt { get; set; }

        [JsonProperty(Order = 33)]
        public TranscriptioToIgnorePattern[] TranscriptionToIgnorePatterns { get; set; }

        [JsonProperty(Order = 34)]
        public bool PromptUserOnError { get; set; } = true;

        protected override string GetMetadataProduced() => this.MetadataProduced;

        protected override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            if (!base.IsPrerequisitesForAudioMet(context, out reason))
            {
                return false;
            }

            reason = null;
            return true;
        }

        protected override void DoWorkInternal(SubtitleGeneratorContext context, Transcription transcription)
        {
            var processStartTime = DateTime.Now;

            var fullPcmAudio = base.GetPcmAudio(context);

            var binaryDataExtractors = new BinaryDataExtractorCachedCollection(
                new[] {
                    new BinaryDataExtractorExtended {
                        Extractor = new BinaryDataExtractorAudio() { OutputFieldName = "Audio" },
                        TrainingContentLists = null,
                        GetData = (section, timing, _, __) =>
                        {
                            context.DefaultProgressUpdateHandler("ffmpeg", $"{timing.StartTime}", $"Generating .wav for {timing.StartTime} to {timing.EndTime}");
                            var tempWavFile = Path.GetTempFileName() + ".wav";
                            var snippet = fullPcmAudio.ExtractSnippet(timing.StartTime, timing.EndTime);
                            context.FfmpegHelper.ConvertPcmAudioToOtherFormat(snippet, tempWavFile);

                            var audioBytes = File.ReadAllBytes(tempWavFile);
                            var data = new[]
                            {
                                new AIRequestPartAudio(
                                    section,
                                    $"{timing.StartTime:hh\\-mm\\-ss\\-fff}.wav",
                                    audioBytes,
                                    snippet.Duration)
                            };
                            File.Delete(tempWavFile);

                            if (this.KeepTemporaryFiles)
                                context.CreateVerboseBinaryFile($"{transcription.Id}_{timing.StartTime:hh\\-mm\\-ss\\-fff}.wav", audioBytes, processStartTime);
                            return data;
                        } 
                    } 
                });

            var jobState = transcription.CurrentJobState as JobState;
            if (jobState == null)
            {
                jobState = new JobState
                {
                    NextStartTime = TimeSpan.Zero
                };
                transcription.CurrentJobState = jobState;
            }

            var requestNumber = 1;
            var nextStartTime = jobState.NextStartTime;
            while (nextStartTime < fullPcmAudio.EndTime)
            {
                var chunkStartTime = nextStartTime;
                var chunkEndTime = nextStartTime + this.MaxChunkDuration;
                var chunkTiming = new Timing(
                        chunkStartTime,
                        chunkEndTime < fullPcmAudio.EndTime ? chunkEndTime : fullPcmAudio.EndTime);
                var request = CreateRequestForFullAudio(
                    context,
                    processStartTime,
                    requestNumber++,
                    chunkTiming,
                    binaryDataExtractors);
                var response = this.Engine.Execute(context, request);
                if (response.AssistantMessage == null)
                {
                    throw new Exception($"'{this.GetType().Name}' does not support AIEngine type '{this.Engine.GetType().Name}'.");
                }
                if (response.Cost != null)
                {
                    dynamic customInfos = response.Cost.CustomInfos ?? new ExpandoObject();
                    customInfos.ChunkStartTime = $"{chunkTiming.StartTime:hh\\:mm\\:ss\\.fff}";
                    customInfos.ChunkEndTime = $"{chunkTiming.EndTime:hh\\:mm\\:ss\\.fff}";
                    customInfos.Duration = $"{chunkTiming.Duration:hh\\:mm\\:ss\\.fff}";
                    response.Cost.CustomInfos = customInfos;
                }
                var itemCountBeforeChunk = transcription.Items.Count;
                HandleResponse(context, transcription, response, chunkStartTime, chunkEndTime); 

                // Items produced by THIS chunk only (don't rely on list order or on previous chunks' items)
                var chunkItems = transcription.Items.Skip(itemCountBeforeChunk).ToList();
                context.WriteInfo($"Current number of subtitles: {transcription.Items.Count}");
                var isLastChunk = chunkEndTime >= fullPcmAudio.EndTime;
                if (isLastChunk)
                {
                    nextStartTime = fullPcmAudio.EndTime;
                }
                else
                {
                    var startOfBufferZone = chunkEndTime - TimeSpan.FromSeconds(10);

                    // Only items that START inside the buffer zone are suspect (possibly cut at the chunk end).
                    // Anything starting before it is a complete subtitle (subtitles are assumed to be < 10s).
                    var itemsToRedo = chunkItems.Where(i => i.StartTime >= startOfBufferZone).ToList();
                    var itemsToKeep = chunkItems.Where(i => i.StartTime < startOfBufferZone).ToList();
                    transcription.Items.RemoveAll(i => itemsToRedo.Contains(i));

                    // 1) Never before the buffer zone (redo <= 10s) and never inside the audio of a kept subtitle
                    var keptEnd = itemsToKeep.Count > 0 ? itemsToKeep.Max(i => i.EndTime) : chunkStartTime;
                    nextStartTime = TimeSpanExtensions.Max(startOfBufferZone, keptEnd);
                    // 2) Never skip the beginning of a subtitle we just removed (only matters with overlapping items)
                    if (itemsToRedo.Count > 0)
                    {
                        nextStartTime = TimeSpanExtensions.Min(nextStartTime, itemsToRedo.Min(i => i.StartTime));
                    }
                    context.WriteInfo($"Buffer zone: kept {itemsToKeep.Count}, will redo {itemsToRedo.Count} item(s), next chunk starts at {nextStartTime:hh\\:mm\\:ss\\.fff}.");
                }
                jobState.NextStartTime = nextStartTime;
                context.WIP.Save();
            }

            transcription.MarkAsFinished();
            context.WIP.Save();
        }

        private AIRequest CreateRequestForFullAudio(
            SubtitleGeneratorContext context,
            DateTime processStartTime,
            int requestNumber,
            ITiming timing,
            BinaryDataExtractorCachedCollection binaryDataExtractors)
        {
            var systemParts = new AIRequestPartCollection();
            var userParts = new AIRequestPartCollection();
            if (this.SystemPrompt != null)
            {
                systemParts.AddText(AIRequestSection.SystemPrompt, this.SystemPrompt.GetFinalText(context.Config.SourceLanguage));
            }

            if (this.UserPrompt != null)
            {
                userParts.AddText(AIRequestSection.SystemValidation, this.UserPrompt.GetFinalText(context.Config.SourceLanguage));
            }

            userParts.AddRange(binaryDataExtractors.GetNamedContentListForTiming(AIRequestSection.PrimaryNodes, timing).First().Value);

            return new AIRequest(
                processStartTime,
                requestNumber,
                this.TranscriptionId,
                null,
                systemParts,
                userParts,
                this.MetadataProduced,
                $"{timing.StartTime:hh\\:mm\\:ss\\.fff} to {timing.EndTime:hh\\:mm\\:ss\\.fff} out of {context.WIP.TimelineMap.Duration:hh\\:mm\\:ss\\.fff}",
                timing.StartTime);
        }

        private class TranscriptionNode
        {
            public string StartTime { get; set; }
            public string EndTime { get; set; }
        }

        private void HandleResponse(
                   SubtitleGeneratorContext context,
                   Transcription transcription,
                   AIResponse response,
                   TimeSpan chunkStartTime,
                   TimeSpan chunkEndTime,
                   string prefix = "")
        {
            transcription.Costs.Add(response.Cost);
            transcription.Costs.AddRange(response.AdditionalCosts);

            static string GetSegmentInformation(JToken segment)
            {
                var lineInfo = (IJsonLineInfo)segment;
                return (lineInfo != null && lineInfo.HasLineInfo())
                    ? $"Error in segment starting at line {lineInfo.LineNumber}, position {lineInfo.LinePosition}."
                    : $"Error in segment at unknown location.";
            }

            string textToParse = response.AssistantMessage;
            while (true)
            {
                string fixedJson = null;
                try
                {
                    fixedJson = AIEngineRunner.TryToFixReceivedJson(response.Request, textToParse, tryToFixEnd: false);
                    JArray responseArray;
                    using (var stringReader = new StringReader(fixedJson))
                    using (var jsonReader = new JsonTextReader(stringReader))
                    {
                        responseArray = JArray.Load(jsonReader);
                    }

                    if (responseArray == null) throw new Exception("Parsed JSON resulted in null.");

                    var pendingItems = new List<TranscribedItem>();
                    foreach (var segment in responseArray)
                    {
                        if (!(segment is JObject segmentObj))
                        {
                            throw new Exception($"Expected JSON object in array. {GetSegmentInformation(segment)}");
                        }

                        var node = segmentObj.ToObject<TranscriptionNode>();

                        if (string.IsNullOrWhiteSpace(node.StartTime))
                        {
                            throw new Exception($"StartTime is missing or empty. {GetSegmentInformation(segment)}");
                        }
                        if (string.IsNullOrWhiteSpace(node.EndTime))
                        {
                            throw new Exception($"EndTime is missing or empty. {GetSegmentInformation(segment)}");
                        }

                        var nodeStartTime = TimeSpanExtensions.FlexibleTimeSpanParse(node.StartTime);
                        var nodeEndTime = TimeSpanExtensions.FlexibleTimeSpanParse(node.EndTime);
                        var startTime = chunkStartTime + nodeStartTime;
                        var endTime = chunkStartTime + nodeEndTime;

                        if (endTime > chunkEndTime + TimeSpan.FromSeconds(1))
                        {
                            throw new Exception($"Received node with endtime {nodeEndTime} [{context.WIP.TimelineMap.ConvertToPartSpecificFileIndexAndTime(endTime)}] when audio chunk end is [{context.WIP.TimelineMap.ConvertToPartSpecificFileIndexAndTime(chunkEndTime)}]. {GetSegmentInformation(segment)}");
                        }

                        // Everything left is metadata
                        var extraMetadatas = new MetadataCollection();
                        foreach (var prop in segmentObj.Properties())
                        {
                            if (prop.Value != null
                              && !string.Equals(prop.Name, nameof(TranscriptionNode.StartTime), StringComparison.OrdinalIgnoreCase)
                              && !string.Equals(prop.Name, nameof(TranscriptionNode.EndTime), StringComparison.OrdinalIgnoreCase))
                            {
                                extraMetadatas[prop.Name] = prop.Value.ToString();
                            }
                        }

                        if (!extraMetadatas.ContainsKey(this.MetadataProduced))
                        {
                            if (extraMetadatas.TryGetValue(this.MetadataProduced, out var vt))
                            {
                                extraMetadatas[this.MetadataProduced] = vt;
                            }
                            else
                            {
                                throw new Exception($"Required metadata '{this.MetadataProduced}' is not present. {GetSegmentInformation(segment)}");
                            }
                        }

                        if (!string.IsNullOrEmpty(prefix))
                        {
                            extraMetadatas[this.MetadataProduced] = prefix + extraMetadatas[this.MetadataProduced];
                        }

                        var voiceText = extraMetadatas.Get(this.MetadataProduced) ?? string.Empty;

                        var pattern = this.TranscriptionToIgnorePatterns?.FirstOrDefault(p => p.Regex.IsMatch(voiceText));
                        if (pattern == null)
                        {
                            pendingItems.Add(new TranscribedItem(
                                startTime,
                                endTime,
                                extraMetadatas));
                        }
                        else
                        {
                            context.WriteInfo($"Ignoring '{pattern.Name}' transcription at [{context.WIP.TimelineMap.ConvertToPartSpecificFileIndexAndTime(startTime)}]: {voiceText}");
                        }
                    }

                    // --- SUCCESS --- 
                    // Only commit to the main transcription if the entire chunk was parsed perfectly
                    transcription.Items.AddRange(pendingItems);
                    response.Cost.NbItemsInResponse += pendingItems.Count;

                    break; // Exit the retry loop
                }
                catch (Exception ex)
                {
                    if (this.PromptUserOnError)
                    {
                        // Show the UI with the partially fixed JSON (if available) or the raw text
                        string userFixedText = JsonCorrectionHelper.PromptForFix(ex.Message, fixedJson ?? textToParse);

                        if (userFixedText != null)
                        {
                            textToParse = userFixedText;
                            continue; // Restart the loop with the user's manual fix
                        }
                        // If they cancel, let it fall through
                    }

                    throw;
                }
            }
        }

        public class TranscriptioToIgnorePattern
        {
            public string Name { get; set; }
            public string Pattern { get; set; }
            private Regex _regex;

            [JsonIgnore]
            public Regex Regex
            {
                get
                {
                    _regex = _regex ?? new Regex($"^({this.Pattern})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
                    return _regex;
                }
            }

            public TranscriptioToIgnorePattern(string name, string pattern)
            {
                this.Name = name;
                this.Pattern = pattern;
                _regex = null;
            }
        }
    }
}