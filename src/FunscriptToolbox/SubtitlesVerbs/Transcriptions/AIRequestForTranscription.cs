using System;
using System.Collections.Generic;
using System.Linq;
using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.AudioExtraction;
using Newtonsoft.Json.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class AIRequestForTranscription : AIRequest
    {
        private readonly Transcription _transcription;
        public PcmAudio[] Items { get; }

        public override string NbItemsString() => $"{this.Items.Length} audio samples";

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
            var transcribedTextsAdded = ParseAndAddTranscription(_transcription, responseReceived, this);
            if (transcribedTextsAdded.Count > 0)
            {
                context.CurrentWipsub.Save();

                // Add transcribed texts to the transcription
                foreach (var tt in transcribedTextsAdded)
                {
                    // Update UI
                    context.DefaultUpdateHandler(
                        "Transcription",
                        this.ToolAction,
                        tt.Text);
                }
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

        public static List<TranscribedText> ParseAndAddTranscription(
            Transcription transcription, 
            string responseReceived,
            AIRequest request = null)
        {
            try
            {
                dynamic transcriptionArray = ParseAndFixJson(request, responseReceived);

                var transcribedTextsAdded = new List<TranscribedText>();
                foreach (var segment in transcriptionArray)
                {
                    var seg = (JObject)segment;

                    // Extract and remove known fields
                    var startTime = TimeSpan.Parse((string)seg["StartTime"]);
                    seg.Remove("StartTime");

                    var endTime = TimeSpan.Parse((string)seg["EndTime"]);
                    seg.Remove("EndTime");

                    var text = ((string)seg["Transcription"])?.Trim() ?? string.Empty;
                    seg.Remove("Transcription");

                    // Everything left is metadata
                    var transcriptionMetadatas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var prop in seg.Properties())
                    {
                        if (prop.Value != null)
                            transcriptionMetadatas[prop.Name] = prop.Value.ToString();
                    }

                    var tt = new TranscribedText(startTime, endTime, text, transcriptionMetadatas);
                    transcribedTextsAdded.Add(tt);
                    transcription.Items.Add(tt);
                }
                return transcribedTextsAdded;
            }
            catch (AIRequestException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new AIRequestException(ex, request, ex.Message);
            }
        }
    }
}