using FunscriptToolbox.Core;
using FunscriptToolbox.Core.Infra;
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
    public class TranscriberOnScreenText : TranscriberAudio
    {
        public TranscriberOnScreenText()
        {
        }

        [JsonProperty(Order = 20, Required = Required.Always)]
        internal MetadataAggregator Metadatas { get; set; }

        [JsonProperty(Order = 21)]
        internal string FfmpegFilter { get; set; }

        [JsonProperty(Order = 30, Required = Required.Always)]
        public AIEngine Engine { get; set; }

        [JsonProperty(Order = 31)]
        public AIOptions Options { get; set; } = new AIOptions();


        public override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            if (Metadatas?.IsPrerequisitesMetIncludingTimings(context, out reason) == false)
            {
                return false;
            }

            reason = null;
            return true;
        }


        public override void Transcribe(
            SubtitleGeneratorContext context,
            Transcription transcription)
        {
            var processStartTime = DateTime.Now;

            var items = this.Metadatas.GetTimingsWithMetadata<byte[]>(context, transcription);
            var itemsToTranscribe = new List<TimedObjectWithMetadata<byte[]>>();
            foreach (var item in items)
            {
                var grabOnScreenTextCommand = item.Metadata.GrabOnScreenText;
                if (grabOnScreenTextCommand != null && item.Metadata.OnScreenText == null)
                {
                    var middleTime = TimeSpan.FromMilliseconds((item.StartTime.TotalMilliseconds + item.EndTime.TotalMilliseconds) / 2);
                    var bytes = context.FfmpegAudioHelper.TakeScreenshotAsBytes(
                        context.CurrentWipsub.OriginalVideoPath,
                        middleTime,
                        ".jpg",
                        this.FfmpegFilter);
                    item.Tag = bytes;
                    itemsToTranscribe.Add(item);
                    context.CreateVerboseBinaryFile($"{transcription.Id}_{middleTime:hhmmssfff}.jpg", bytes, processStartTime);
                }
            }

            TranscribeImages(
                context,
                transcription,
                itemsToTranscribe.ToArray());

            if (!transcription.Items.Any(item => item.Metadata.IsVoice && item.Metadata.VoiceText == null)
                && !items.Any(f => f.Metadata.IsVoice && !transcription.Items.Any(k => k.StartTime == f.StartTime)))
            {
                transcription.MarkAsFinished();
            }

            // Save verbose output if needed
            if (context.IsVerbose)
            {
                var srt = new SubtitleFile();
                srt.Subtitles.AddRange(transcription.Items.Select(item =>
                    new Subtitle(
                        item.StartTime,
                        item.EndTime,
                        item.Text + "\n" + string.Join("\n", item.Metadata.Select(kvp => $"{{{kvp.Key}:{kvp.Value}}}")))));
                srt.SaveSrt(context.GetPotentialVerboseFilePath($"{transcription.Id}.srt", DateTime.Now));
            }
        }

        public void TranscribeImages(
            SubtitleGeneratorContext context,
            Transcription transcription,
            TimedObjectWithMetadata<byte[]>[] items)
        {
            var nbErrors = HandlePreviousFiles(context, transcription);
            if (nbErrors == 0)
            {
                try
                {
                    this.Engine.Execute(context, CreateRequests(
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
                        var nbAdded = AIRequestForTranscription<byte[]>.ParseAndAddTranscription(transcription, response);
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

        private IEnumerable<AIRequest> CreateRequests(
            Transcription transcription,
            TimedObjectWithMetadata<byte[]>[] itemsToTranscribe)
        {
            var contentList = new List<dynamic>();
            var messages = new List<dynamic>();
            if (Options.SystemPrompt != null)
            {
                messages.Add(new
                {
                    role = "system",
                    content = Options.SystemPrompt.GetFinalText(transcription.Language, transcription.Language)
                });
            }
            if (Options.FirstUserPrompt != null)
            {
                contentList.Add(new
                {
                    type = "text",
                    text = Options.FirstUserPrompt.GetFinalText(transcription.Language, transcription.Language)
                });
            }
            foreach (var item in itemsToTranscribe)
            {
                item.Metadata.Add("StartTime", item.StartTime.ToString(@"hh\:mm\:ss\.fff"));
                item.Metadata.Add("EndTime", item.EndTime.ToString(@"hh\:mm\:ss\.fff"));
                contentList.Add(new
                {
                    type = "text",
                    text = JsonConvert.SerializeObject(item.Metadata, Formatting.Indented)
                });
                contentList.Add(CreateInputImage(item.Tag));
            }

            messages.Add(new
            {
                role = "user",
                content = contentList.ToArray()
            });

            yield return new AIRequestForTranscription<byte[]>(
                $"{transcription.Id}",
                $"images",
                0,
                messages,
                transcription,
                itemsToTranscribe,
                "images");
        }

        private static dynamic CreateInputImage(byte[] image)
        {
            var base64Image = Convert.ToBase64String(image);
            return new
            {
                type = "image_url",
                image_url = new
                {
                    url = $"data:image/jpeg;base64,{base64Image}"
                }
            };
        }
    }
}