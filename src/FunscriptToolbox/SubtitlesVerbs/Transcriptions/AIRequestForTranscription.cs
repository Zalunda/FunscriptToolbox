using System;
using System.Collections.Generic;
using System.Linq;
using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.AudioExtraction;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class AIRequestForTranscription : AIRequest
    {
        private readonly Transcription _transcription;
        public PcmAudio[] Items { get; }

        public AIRequestForTranscription(
            string taskId,
            string toolAction,
            int requestNumber,
            List<dynamic> messages,
            Transcription transcription,
            PcmAudio[] audios)
            : base(taskId, toolAction, requestNumber, messages)
        {
            _transcription = transcription;
            Items = audios;
        }

        public override void HandleResponse(
            SubtitleGeneratorContext context,
            string taskName,
            TimeSpan timeTaken,
            string responseReceived,
            int? promptTokens = null,
            int? completionTokens = null,
            int? totalTokens = null)
        {
            var transcribedTexts = ParseApiResponse(context, responseReceived);

            // Add transcribed texts to the transcription
            foreach (var text in transcribedTexts)
            {
                _transcription.Items.Add(text);

                // Update UI
                context.DefaultUpdateHandler(
                    "Transcription",
                    this.ToolAction,
                    text.Text);
            }

            // Add the cost to the transcription
            _transcription.Costs.Add(
                new TranscriptionCost(
                    taskName,
                    timeTaken,
                    Items.Length,
                    Items.Sum(a => a.Duration),
                    promptTokens,
                    completionTokens,
                    totalTokens));
        }

        public override string NbItemsString() => $"{this.Items.Length} audio samples";

        private List<TranscribedText> ParseApiResponse(SubtitleGeneratorContext context, string jsonResponse)
        {
            var transcribedTexts = new List<TranscribedText>();
            try
            {
                
                dynamic transcriptionArray = ParseAndFixJson(jsonResponse);

                foreach (var segment in transcriptionArray)
                {
                    var text = (string)segment.Transcription ?? (string)segment.Text;
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    var startTime = TimeSpan.Parse((string)segment.StartTime);
                    var endTime = TimeSpan.Parse((string)segment.EndTime);

                    transcribedTexts.Add(
                        new TranscribedText(startTime, endTime, text.Trim()));
                }
            }
            catch (Exception ex)
            {
                context.WriteError($"Failed to parse API response: {ex.Message}");
                throw new AIEngineException(ex, jsonResponse);
            }

            return transcribedTexts;
        }
    }
}