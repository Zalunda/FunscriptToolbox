using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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

        [JsonProperty(Order = 2)]
        public AIPrompt UserPrompt { get; set; }

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

        protected override void Transcribe(
            SubtitleGeneratorContext context,
            Transcription transcription)
        {
            var processStartTime = DateTime.Now;

            var fullPcmAudio = base.GetPcmAudio(context);

            var binaryGenerator = new CachedBinaryGenerator((timing) =>
            {
                context.DefaultProgressUpdateHandler("ffmpeg", $"{timing.StartTime}", $"Generating .wav for {timing.StartTime} to {timing.EndTime}");
                var tempWavFile = Path.GetTempFileName() + ".wav";
                context.FfmpegAudioHelper.ConvertPcmAudioToOtherFormat(
                    fullPcmAudio.ExtractSnippet(timing.StartTime, timing.EndTime), tempWavFile);

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
                if (this.KeepTemporaryFiles)
                    context.CreateVerboseBinaryFile($"{transcription.Id}_{timing.StartTime:hhmmssfff}.wav", audioBytes, processStartTime);
                return data;
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
                    requestNumber,
                    new Timing(
                        chunkStartTime,
                        chunkEndTime < fullPcmAudio.EndTime ? chunkEndTime : fullPcmAudio.EndTime),
                    binaryGenerator);
                var response = this.Engine.Execute(context, request);
                HandleResponse(transcription, response, chunkStartTime);
                nextStartTime = transcription.Items.LastOrDefault()?.EndTime ?? chunkStartTime;

                var startOfBufferZone = chunkEndTime - TimeSpan.FromSeconds(10);
                if (nextStartTime < startOfBufferZone)
                {
                    nextStartTime = startOfBufferZone;
                }
                else
                {
                    transcription.Items.Remove(transcription.Items.Last());
                    nextStartTime = transcription.Items.LastOrDefault()?.EndTime ?? startOfBufferZone;
                }

                jobState.NextStartTime = nextStartTime;
                context.WIP.Save();
            }

            transcription.MarkAsFinished();
            context.WIP.Save();
        }

        private AIRequest CreateRequestForFullAudio(
            SubtitleGeneratorContext context,
            int requestNumber,
            ITiming timing,
            CachedBinaryGenerator binaryGenerator)
        {
            var messages = new List<dynamic>();
            var contentList = new List<dynamic>();
            if (this.SystemPrompt != null)
            {
                messages.Add(new
                {
                    role = "system",
                    content = this.SystemPrompt.GetFinalText(context.OverrideSourceLanguage)
                });
            }

            if (this.UserPrompt != null)
            {
                contentList.Add(new
                {
                    type = "text",
                    text = this.UserPrompt.GetFinalText(context.OverrideSourceLanguage)
                });
            }

            contentList.AddRange(binaryGenerator.GetBinaryContent(timing));

            messages.Add(new
            {
                role = "user",
                content = contentList.ToArray()
            });

            return new AIRequest(
                requestNumber,
                this.SourceAudioId,
                messages,
                0,
                this.MetadataProduced);
        }

        private void HandleResponse(
            Transcription transcription,
            AIResponse response,
            TimeSpan offset,
            string prefix = "")
        {
            foreach (var node in JsonConvert.DeserializeObject<dynamic>(AIEngineRunner.TryToFixReceivedJson(
                                    response.AssistantMessage,
                                    tryToFixEnd: false)))
            {
                transcription.Items.Add(
                    new TranscribedItem(
                        offset + AIEngineRunner.LooseTimeSpanParse((string)node.StartTime),
                        offset + AIEngineRunner.LooseTimeSpanParse((string)node.EndTime), 
                        MetadataCollection.CreateSimple(this.MetadataProduced, prefix + (string)node.VoiceText)));
            }
        }
    }
}