using FunscriptToolbox.Core;
using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.AudioExtraction;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscriberToolLLMMultimodalAPI : TranscriberTool
    {
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
            var nbErrors = HandlePreviousFiles(context, transcription);
            if (nbErrors == 0)
            {
                try
                {
                    var lastItem = transcription.Items.LastOrDefault();
                    this.Engine.Execute(context, CreateRequests(
                            context,
                            transcription,
                            audios.Where(audio => lastItem == null || audio.Offset >= lastItem.EndTime).ToArray()));

                    // Save verbose output if needed
                    if (context.IsVerbose)
                    {
                        var srt = new SubtitleFile();
                        srt.Subtitles.AddRange(transcription.Items.Select(t => new Subtitle(t.StartTime, t.EndTime, t.Text)));
                        srt.SaveSrt(context.GetPotentialVerboseFilePath($"{filesPrefix}llm-multimodal.srt", DateTime.Now));
                    }
                }
                catch (AIRequestException ex)
                {
                    var filepath = ex.Request.GetFilenamePattern(context.CurrentBaseFilePath);
                    context.SoftDelete(filepath);
                    var body = ex.ResponseBodyPartiallyFixed ?? $"Original Prompt:\n\n{ex.Request?.FullPrompt}";
                    File.WriteAllText(filepath, $"{ex.Message.Replace("[", "(").Replace("]", ")")}\n\n{body}", Encoding.UTF8);
                    context.AddUserTodo($"Manually fix the following error in file '{Path.GetFileName(filepath)}':\n{ex.Message}");
                    throw;
                }
                catch (Exception ex) when (ex is AggregateException || ex is HttpRequestException)
                {
                    context.WriteError($"Error while communicating with the API: {ex.Message}");
                    context.WriteLog(ex.ToString());
                    throw new AIRequestException(ex, null, $"Error while communicating with the 'client.BaseAddress' API: {ex.Message}");
                }
            }
        }

        private int HandlePreviousFiles(
            SubtitleGeneratorContext context,
            Transcription transcription)
        {
            var nbErrors = 0;
            var patternSuffix = "_\\d+\\.txt";

            foreach (var fullpath in Directory.GetFiles(
                PathExtension.SafeGetDirectoryName(context.CurrentBaseFilePath),
                "*.*"))
            {
                var filename = Path.GetFileName(fullpath);
                if (Regex.IsMatch(
                    filename,
                    $"^" + Regex.Escape($"{Path.GetFileName(context.CurrentBaseFilePath)}.TODO_{transcription.Id}") + $"{patternSuffix}$",
                    RegexOptions.IgnoreCase))
                {
                    var response = File.ReadAllText(fullpath);
                    context.SoftDelete(fullpath);

                    try
                    {
                        context.WriteInfo($"        Analysing existing file '{filename}'...");
                        var nbAdded = AIRequestForTranscription.ParseAndAddTranscription(transcription, response);
                        context.WriteInfo($"        Finished:");
                        context.WriteInfo($"            Nb transcriptions added: {nbAdded}");
                        context.CurrentWipsub.Save();
                    }
                    catch (AIRequestException ex)
                    {
                        nbErrors++;
                        File.WriteAllText(fullpath, $"{ex.Message.Replace("[", "(").Replace("]", ")")}\n\n{ex.ResponseBodyPartiallyFixed}", Encoding.UTF8);
                        context.WriteInfo($"Error while parsing file '{filename}':{ex.Message}");
                        context.AddUserTodo($"Manually fix the following error in file '{filename}':\n{ex.Message}");
                    }
                }
            }

            return nbErrors;
        }

        private IEnumerable<AIRequestForTranscription> CreateRequests(
            SubtitleGeneratorContext context,
            Transcription transcription,
            PcmAudio[] audios)
        {
            var totalBatches = (audios.Length + BatchSize - 1) / BatchSize;
            var requestNumber = 1;
            var forcedTiming = context.CurrentWipsub?.SubtitlesForcedTiming;

            for (int i = 0; i < audios.Length; i += BatchSize)
            {
                var audioBatch = audios.Skip(i).Take(BatchSize).ToArray();
                var batchId = (i / BatchSize) + 1;

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
                    $"{transcription.Id}",
                    $"{batchId}/{totalBatches}",
                    requestNumber++,
                    messages,
                    transcription,
                    audioBatch);
            }
        }
    }
}