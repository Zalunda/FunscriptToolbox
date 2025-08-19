using CommandLine;
using FunscriptToolbox.Core;
using FunscriptToolbox.SubtitlesVerbs.AudioExtraction;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscriberToolLLMMultimodalAPI : TranscriberTool
    {
        private const string ToolName = "LLMMultimodalAPI";

        [JsonProperty(Order = 1)]
        public int BatchSize { get; set; } = 10;

        [JsonProperty(Order = 2, Required = Required.Always)]
        public AIEngine Engine { get; set; }

        [JsonProperty(Order = 3)]
        public AIOptions Options { get; set; } = new AIOptions();

        public override void TranscribeAudio(
            SubtitleGeneratorContext context,
            ProgressUpdateDelegate progressUpdateCallback,
            Transcription transcription,
            PcmAudio[] audios,
            string filesPrefix)
        {
            // Execute all requests
            if (!this.Engine.Execute(context, CreateRequests(
                    context,
                    transcription,
                    filesPrefix,
                    audios,
                    progressUpdateCallback)))
            {
                throw new Exception("TODO");
            }

            // Save verbose output if needed
            if (context.IsVerbose)
            {
                var srt = new SubtitleFile();
                srt.Subtitles.AddRange(transcription.Items.Select(t => new Subtitle(t.StartTime, t.EndTime, t.Text)));
                srt.SaveSrt(context.GetPotentialVerboseFilePath($"{filesPrefix}llm-multimodal.srt", DateTime.Now));
            }
        }

        private IEnumerable<AIRequestForTranscription> CreateRequests(
            SubtitleGeneratorContext context,
            Transcription transcription,
            string filesPrefix,
            PcmAudio[] audios,
            ProgressUpdateDelegate progressUpdateCallback)
        {
            var totalBatches = (audios.Length + BatchSize - 1) / BatchSize;
            var requestNumber = 1;
            var forcedTiming = context.CurrentWipsub?.SubtitlesForcedTiming;

            for (int i = 0; i < audios.Length; i += BatchSize)
            {
                var audioBatch = audios.Skip(i).Take(BatchSize).ToArray();
                var batchId = (i / BatchSize) + 1;

                progressUpdateCallback?.Invoke(ToolName, $"{batchId}/{totalBatches}", $"Preparing batch {batchId} of {totalBatches}...");

                var messages = new List<dynamic>();

                // Add system prompt if configured
                if (Options.SystemPrompt != null)
                {
                    messages.Add(new
                    {
                        role = "system",
                        content = Options.SystemPrompt.GetFinalText(transcription.Language, transcription.Language)
                    });
                }

                // Build user message with audio and metadata
                var contentList = new List<dynamic>();

                // Add user prompt if configured
                if (Options.FirstUserPrompt != null)
                {
                    contentList.Add(new
                    {
                        type = "text",
                        text = Options.FirstUserPrompt.GetFinalText(transcription.Language, transcription.Language)
                    });
                }
                string ongoingContext = null;

                // Add each audio with its metadata
                foreach (var audio in audioBatch)
                {
                    // Use the shared helper to create metadata
                    var metadata = this.Options.CreateMetadata(
                        forcedTiming,
                        audio.Offset,
                        audio.Offset + audio.Duration,
                        ref ongoingContext);

                    // Add metadata as JSON if we have any
                    if (metadata.Count > 0)
                    {
                        contentList.Add(new
                        {
                            type = "text",
                            text = JsonConvert.SerializeObject(metadata, Formatting.None)
                        });
                    }

                    // Prepare audio data
                    var tempWavFile = Path.GetTempFileName() + ".wav";
                    context.FfmpegAudioHelper.ConvertPcmAudioToWavFile(audio, tempWavFile);

                    var audioBytes = File.ReadAllBytes(tempWavFile);
                    var base64Audio = Convert.ToBase64String(audioBytes);
                    File.Delete(tempWavFile);

                    // Add audio data
                    contentList.Add(new
                    {
                        type = "input_audio",
                        input_audio = new
                        {
                            data = base64Audio,
                            format = "wav"
                        }
                    });
                }

                messages.Add(new
                {
                    role = "user",
                    content = contentList.ToArray()
                });

                yield return new AIRequestForTranscription(
                    $"{filesPrefix}-batch{batchId:D03}",
                    $"{batchId}/{totalBatches}",
                    requestNumber++,
                    messages,
                    transcription,
                    audioBatch);
            }
        }
    }
}