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
    public class TranscriberAudioToolMultimodalAI : TranscriberAudioTool
    {
        [JsonProperty(Order = 1)]
        public int BatchSize { get; set; } = 100000;

        [JsonProperty(Order = 2, Required = Required.Always)]
        public AIEngine Engine { get; set; }

        [JsonProperty(Order = 3)]
        public AIOptions Options { get; set; } = new AIOptions();

        public override void TranscribeAudio(
            SubtitleGeneratorContext context,
            Transcription transcription,
            TimedObjectWithMetadata<PcmAudio>[] items)
        {
            var nbErrors = HandlePreviousFiles(context, transcription);
            if (nbErrors == 0)
            {
                try
                {
                    this.Engine.Execute(context, CreateRequests(
                            context,
                            transcription,
                            items));
                }
                catch (AIRequestException ex)
                {
                    var filepath = ex.Request.GetFilenamePattern(context.CurrentBaseFilePath);
                    if (ex.ResponseBodyPartiallyFixed == null)
                    {
                        filepath.Replace("TODO_", "ERROR_");
                    }
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
                        context.WriteInfo($"            Nb transcriptions added: {nbAdded.Count}");
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
            TimedObjectWithMetadata<PcmAudio>[] items)
        {
            var itemsWithAudio = items.Where(f => f.Tag != null || f.Metadata.VoiceText != null).ToArray();
            var itemsWithAudioToTranscribe = itemsWithAudio.Where(f => f.Metadata.VoiceText == null).ToArray();
            var itemsForSpeakerTraining = itemsWithAudio.Where(f => f.Metadata.SpeakerTraining != null).ToArray();
            var isFirstExecution = itemsWithAudioToTranscribe.Length == itemsWithAudio.Length;

            var trainingContentList = new List<dynamic>();
            if (itemsForSpeakerTraining.Length > 0)
            {
                trainingContentList.Add(new
                {
                    type = "text",
                    text = "Since this is a continuation request and the training audio where in a previous part, here are a few audio segments for diarization learning (speaker name followed by an audio of the person voice):"
                });
                foreach (var item in itemsForSpeakerTraining)
                {
                    trainingContentList.Add(new
                    {
                        type = "text",
                        text = $"Speaker: {item.Metadata.SpeakerTraining}"
                    });
                    trainingContentList.Add(CreateInputAudio(context, item.Tag));
                }
            }

            var itemsContentList = new List<dynamic>();
            var batchId = 0;
            var itemsInRequest = new List<TimedObjectWithMetadata<PcmAudio>>();
            var index = 0;
            foreach (var item in items)
            {
                index++;
                item.Metadata.Add("StartTime", item.StartTime.ToString(@"hh\:mm\:ss\.fff"));
                item.Metadata.Add("EndTime", item.EndTime.ToString(@"hh\:mm\:ss\.fff"));
                itemsContentList.Add(new
                {
                    type = "text",
                    text = JsonConvert.SerializeObject(item.Metadata, Formatting.Indented)
                });
                if (item.Tag != null && item.Metadata.VoiceText == null)
                {
                    itemsContentList.Add(CreateInputAudio(context, item.Tag));
                    itemsInRequest.Add(item);
                }

                if (itemsInRequest.Count == BatchSize || (index == items.Length && itemsInRequest.Count > 0))
                {
                    var messages = new List<dynamic>();
                    if (Options.SystemPrompt != null)
                    {
                        messages.Add(new
                        {
                            role = "system",
                            content = Options.SystemPrompt.GetFinalText(transcription.Language, transcription.Language)
                        });
                    }
                    var finalContentList = new List<dynamic>();
                    if (Options.FirstUserPrompt != null)
                    {
                        finalContentList.Add(new
                        {
                            type = "text",
                            text = Options.FirstUserPrompt.GetFinalText(transcription.Language, transcription.Language)
                        });
                    }
                    if (!isFirstExecution || batchId > 0)
                    {
                        finalContentList.AddRange(trainingContentList);
                    }
                    finalContentList.AddRange(itemsContentList);
                    messages.Add(new
                    {
                        role = "user",
                        content = finalContentList.ToArray()
                    });

                    batchId++;
                    yield return new AIRequestForTranscription(
                        $"{transcription.Id}",
                        $"{batchId}",
                        batchId,
                        messages,
                        transcription,
                        itemsInRequest.ToArray());

                    itemsContentList.Clear();
                    itemsInRequest.Clear();
                }
            }
        }

        private static dynamic CreateInputAudio(SubtitleGeneratorContext context, PcmAudio audio)
        {
            var tempWavFile = Path.GetTempFileName() + ".wav";
            context.FfmpegAudioHelper.ConvertPcmAudioToWavFile(audio, tempWavFile);

            var audioBytes = File.ReadAllBytes(tempWavFile);
            var base64Audio = Convert.ToBase64String(audioBytes);
            File.Delete(tempWavFile);
            return new
                {
                    type = "input_audio",
                    input_audio = new
                    {
                        data = base64Audio,
                        format = "wav"
                    }
                };
        }
    }
}