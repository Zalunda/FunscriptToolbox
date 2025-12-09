using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;
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

        public string[] TranscriptionToIgnorePatterns { get; set; }

        protected override string GetMetadataProduced() => this.MetadataProduced;

        private Regex[] _transcriptionsToIgnoreRegexes;

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
            _transcriptionsToIgnoreRegexes ??= this.TranscriptionToIgnorePatterns?.Select(t => new Regex($"^{t}$", RegexOptions.IgnoreCase | RegexOptions.Compiled)).ToArray() ?? Array.Empty<Regex>();

            var processStartTime = DateTime.Now;

            var fullPcmAudio = base.GetPcmAudio(context);

            var binaryDataExtractors = new BinaryDataExtractorCachedCollection(
                new[] {
                    new BinaryDataExtractorExtended {
                        Extractor = new BinaryDataExtractorAudio() { OutputFieldName = "Audio" },
                        TrainingContentLists = null,
                        GetData = (section, timing, _) =>
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
                var request = CreateRequestForFullAudio(
                    context,
                    processStartTime,
                    requestNumber++,
                    new Timing(
                        chunkStartTime,
                        chunkEndTime < fullPcmAudio.EndTime ? chunkEndTime : fullPcmAudio.EndTime),
                    binaryDataExtractors);
                var response = this.Engine.Execute(context, request);
                if (response.AssistantMessage == null)
                {
                    throw new Exception($"'{this.GetType().Name}' does not support AIEngine type '{this.Engine.GetType().Name}'.");
                }
                HandleResponse(context, transcription, response, chunkStartTime, chunkEndTime);
                nextStartTime = transcription.Items.LastOrDefault()?.EndTime ?? chunkStartTime;

                var startOfBufferZone = chunkEndTime - TimeSpan.FromSeconds(10);
                if (nextStartTime < startOfBufferZone)
                {
                    nextStartTime = startOfBufferZone;
                }
                else
                {
                    transcription.Items.Remove(transcription.Items.Last());
                    var lastEndTime = transcription.Items.LastOrDefault()?.EndTime ?? startOfBufferZone;
                    nextStartTime = lastEndTime > startOfBufferZone ? lastEndTime : startOfBufferZone;
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
                $"{timing.StartTime} to {timing.EndTime} out of {context.WIP.TimelineMap.Duration}",
                timing.StartTime);
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
            foreach (var node in JsonConvert.DeserializeObject<dynamic>(AIEngineRunner.TryToFixReceivedJson(
                                    response.Request,
                                    response.AssistantMessage,
                                    tryToFixEnd: false)))
            {
                var startTime = chunkStartTime + TimeSpanExtensions.FlexibleTimeSpanParse((string)node.StartTime);
                var endTime = chunkStartTime + TimeSpanExtensions.FlexibleTimeSpanParse((string)node.EndTime);
                if (endTime > chunkEndTime + TimeSpan.FromSeconds(1))
                {
                    throw new Exception($"Received node with endtime {(string)node.EndTime} when audio chunk end is {chunkEndTime}.");
                }

                if (!_transcriptionsToIgnoreRegexes.Any(regex => regex.IsMatch(node.VoiceText)))
                {
                    transcription.Items.Add(
                        new TranscribedItem(
                            startTime,
                            endTime,
                            MetadataCollection.CreateSimple(this.MetadataProduced, prefix + (string)node.VoiceText)));
                }
                else
                {
                    context.WriteInfo($"Ignoring node at {startTime:hh\\:mm\\:ss\\.fff}: {node.VoiceText}");
                }
                response.Cost.NbItemsInResponse++;
            }
        }
    }
}