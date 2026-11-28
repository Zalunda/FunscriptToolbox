using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.Progress;
using FunscriptToolbox.SubtitlesVerbs.Inputs;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerbs.Processors
{
    [ConfigDoc("A transcription of the whole audio (SourceAudioId) by an AI that listens to it: the audio is sent in chunks (MaxChunkDuration), the AI answers the subtitles with their timings (the prompts: SystemPrompt / UserPrompt), in the language of the video. What starts at the end of a chunk is done again by the next one.",
        Advice = "The engine must give exact timings: some API / models shorten the audio (silences removed) before the model hears it, and their timings are wrong.")]
    public class TranscriberAudioFullAI : TranscriberAudio
    {
        public class JobState
        {
            public TimeSpan NextStartTime { get; set; }
        }

        [ConfigDoc("The metadata the text goes in (ex. \"VoiceText\").")]
        [JsonProperty(Order = 20, Required = Required.Always)]
        public string MetadataProduced { get; set; }

        [ConfigDoc("The audio is sent in chunks of at most this duration (one request each).",
            Advice = "Shorter chunks: the timings drift less toward the end of a chunk, but more requests.")]
        [JsonProperty(Order = 21)]
        public TimeSpan MaxChunkDuration { get; set; } = TimeSpan.FromMinutes(15);

        [ConfigDoc("Keeps the audio chunks sent to the AI in the backup folder of the scene.")]
        [JsonProperty(Order = 22)]
        public bool KeepTemporaryFiles { get; set; } = false;

        [ConfigDoc("The AI engine that transcribes the whole audio.",
            Advice = "An engine that receives the audio as is: the Google API (on Poe, or with some models, the silences can be removed before the model hears the audio: the timings are then wrong).")]
        [JsonProperty(Order = 30, Required = Required.Always)]
        public AIEngine Engine { get; set; }

        [ConfigDoc("The system prompt: the instructions to the AI.")]
        [JsonProperty(Order = 32)]
        public AIPrompt SystemPrompt { get; set; }

        [ConfigDoc("The user prompt, sent with the audio.")]
        [JsonProperty(Order = 33)]
        public AIPrompt UserPrompt { get; set; }

        [ConfigDoc("A subtitle whose whole text matches one of these patterns is left out of the result (ex. a phrase the AI writes on silences). Each pattern: { \"Name\", \"Pattern\" }.")]
        [JsonProperty(Order = 34)]
        public TranscriptioToIgnorePattern[] TranscriptionToIgnorePatterns { get; set; }

        [ConfigDoc("When the answer of the AI can't be read (ex. broken JSON), the web UI asks you to fix the text (false: an error right away).")]
        [JsonProperty(Order = 35)]
        public bool PromptUserOnError { get; set; } = true;

        protected override string GetMetadataProduced() => this.MetadataProduced;

        protected override void DoWorkInternal(SubtitleGeneratorContext context, Track transcription)
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
                            var snippet = fullPcmAudio.ExtractSnippet(timing.StartTime, timing.EndTime);
                            var audioBytes = snippet.ToWavBytes();
                            var data = new[]
                            {
                                new AIRequestPartAudio(
                                    section,
                                    $"{timing.StartTime:hh\\-mm\\-ss\\-fff}.wav",
                                    audioBytes,
                                    snippet.Duration,
                                    timing.StartTime)
                            };

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
            var isStopAfterRequested = false;
            while (nextStartTime < fullPcmAudio.EndTime)
            {
                // (safe point: the previous chunk is saved)
                // A 'Stop now' that came too late (ex. the batch had already finished, its response is kept): the worker stops here.
                if (isStopAfterRequested)
                {
                    throw new UserStoppedWorkerException("Stopped after the request, as asked by the user.");
                }
                // When the application is closing, no new request is sent.
                // (also 'Pause scene', and 'Pause after this request' on the previous request, which is the same: it waits here)
                context.PauseIfClosing();

                var chunkStartTime = nextStartTime;
                var chunkEndTime = nextStartTime + this.MaxChunkDuration;
                var chunkTiming = new Timing(
                        chunkStartTime,
                        chunkEndTime < fullPcmAudio.EndTime ? chunkEndTime : fullPcmAudio.EndTime);
                // (the number of the chunk is the number of its request)
                var chunkName = $"chunk #{requestNumber}, {chunkTiming.StartTime:hh\\:mm\\:ss} to {chunkTiming.EndTime:hh\\:mm\\:ss} of {fullPcmAudio.EndTime:hh\\:mm\\:ss}";
                var request = context.RunStep(
                    $"Prepare {chunkName}",
                    _ => CreateRequestForFullAudio(
                        context,
                        processStartTime,
                        requestNumber++,
                        chunkTiming,
                        binaryDataExtractors));
                // A pause asked while the chunk was being prepared: it waits here, before the request is sent.
                context.PauseIfClosing();
                AIResponse response;
                // (the name of the transcription is already the name of the parent task)
                using (var requestNode = context.BeginTask("AI request", chunkName))
                {
                    requestNode.SetCancellable(AIEngineAPI.StopNowHint);
                    requestNode.SetPauseAfterSupported();
                    requestNode.SetStopNowWarning(AIEngineAPI.StopNowCostWarning);
                    try
                    {
                        response = this.Engine.Execute(context, request);
                        isStopAfterRequested = requestNode.IsStopAfterRequested;
                    }
                    catch (UserStoppedWorkerException)
                    {
                        requestNode.SetState(TaskState.Cancelled, "stopped by the user");
                        throw;
                    }
                    catch (NeedsUserException ex)
                    {
                        // (not sent: the step waits for the user, ex. an API key)
                        requestNode.SetState(TaskState.Skipped, ex.Message);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        requestNode.Fail(ex.Message);
                        throw;
                    }
                }
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
                context.RunStep("Read the response", _ => HandleResponse(context, transcription, response, chunkStartTime, chunkEndTime));

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
                this.Id,
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
                   Track transcription,
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

                    var pendingItems = new List<TimedItemWithMetadata>();
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
                            pendingItems.Add(new TimedItemWithMetadata(
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
                        string userFixedText = JsonCorrectionHelper.PromptForFix(context, ex.Message, fixedJson ?? textToParse);

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
            [ConfigDoc("The name of the pattern (shown in the warning, ex. \"MaybePointless:Kimochii\").")]
            public string Name { get; set; }
            [ConfigDoc("A regular expression on the text as it is (not case sensitive). It should take its own trailing spaces / punctuation (ex. \"[、。\\\\s…!！]*\"), so that the patterns can follow each other.")]
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