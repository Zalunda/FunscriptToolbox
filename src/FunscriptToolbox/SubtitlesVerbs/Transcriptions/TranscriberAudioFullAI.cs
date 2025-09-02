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

            var requestNumber = 1;
            var nextStartTime = TimeSpan.Zero;
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

                context.WIP.Save();
            }

            TimeSpan minRetryBlockDuration = TimeSpan.FromSeconds(15);
            TimeSpan retryBlockSize = TimeSpan.FromSeconds(10000);
            TimeSpan lastEnd = TimeSpan.Zero;
            var xItem = 0;
            var xDuration = TimeSpan.Zero;
                
            foreach (var item in transcription.Items.ToArray())
            {
                var gapSize = item.StartTime - lastEnd;
                if (gapSize > minRetryBlockDuration)
                {
                    //    xItem++;
                    //    xDuration += gapSize;
                    //}
                    //else
                    //{
                    var bufferZoneDuration = TimeSpan.FromSeconds(2);
                    var nextGapStartTime = lastEnd;
                    var gapEndTime = nextGapStartTime + retryBlockSize + bufferZoneDuration;
                    //var nbRetryBlockcomputedretryBlockSize = ((gapSize.TotalMilliseconds / retryBlockSize.TotalMilliseconds) + 1);
                    //var x = 

                    while (nextGapStartTime < item.StartTime)
                    {
                        var request = CreateRequestForFullAudio(
                            context,
                            requestNumber,
                            new Timing(
                                nextGapStartTime,
                                gapEndTime > item.StartTime ? item.StartTime : gapEndTime),
                            binaryGenerator);
                        var response = this.Engine.Execute(context, request);
                        HandleResponse(transcription, response, nextGapStartTime, "GAP: ");
                        nextGapStartTime = transcription.Items.LastOrDefault()?.EndTime ?? TimeSpan.Zero;

                        var startOfBufferZone = gapEndTime - bufferZoneDuration;
                        if (nextGapStartTime < startOfBufferZone)
                        {
                            nextGapStartTime = startOfBufferZone;
                        }
                        else
                        {
                            transcription.Items.Remove(transcription.Items.Last());
                            nextGapStartTime = transcription.Items.LastOrDefault()?.EndTime ?? startOfBufferZone;
                        }
                    }
                }
                lastEnd = item.EndTime;
            }
            // TODO Last gap at end of scene

            transcription.MarkAsFinished();
            context.WIP.Save();

            SaveDebugSrtIfVerbose(context, transcription);
        }

        private void HandleResponse(
            Transcription transcription,
            AIResponse response,
            TimeSpan offset,
            string prefix = "")
        {
            foreach (var node in JsonConvert.DeserializeObject<dynamic>(AIEngineRunner.TryToFixReceivedJson(
                                    response.Request,
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
    }
}