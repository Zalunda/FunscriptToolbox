using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.Transcriptions;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerbs.Translations
{
    public class TranslatorAI : Translator
    {
        [JsonProperty(Order = 10, Required = Required.Always)]
        public AIEngine Engine { get; set; }

        [JsonProperty(Order = 11, Required = Required.Always)]
        public AIOptions Options { get; set; }

        [JsonProperty(Order = 12)]
        public string PreviousTranslationId { get; set; }

        [JsonProperty(Order = 13)]
        public int MaxItemsInRequest { get; set; } = 10000;

        [JsonProperty(Order = 14)]
        public int IncludePreviousItems { get; set; } = 0;

        [JsonProperty(Order = 15)]
        public int OverlapItemsInRequest { get; set; } = 0;

        public override bool IsPrerequisitesMet(
            Transcription transcription,
            out string reason)
        {
            var previousTranslation = transcription.Translations.FirstOrDefault(f => f.Id == PreviousTranslationId);
            if (PreviousTranslationId != null && previousTranslation != null && !previousTranslation.IsFinished(transcription))
            {
                reason = $"previous translation '{PreviousTranslationId}' is not finished yet.";
                return false;
            }

            reason = null;
            return true;
        }

        public override void Translate(
            SubtitleGeneratorContext context,
            Transcription transcription,
            Translation translation)
        {
            var nbErrors = HandlePreviousFiles(context, transcription, translation);
            if (nbErrors == 0)
            {
                var requests = CreateRequests(context, transcription, translation).ToArray();
                this.Engine.Execute(context, requests);
            }
        }

        private IEnumerable<AIRequest> CreateRequests(
            SubtitleGeneratorContext context,
            Transcription transcription,
            Translation translation)
        {
            var forcedTiming = context.CurrentWipsub?.SubtitlesForcedTiming;

            // Get analysis for parts if needed
            var analysis = Options.IncludeParts && forcedTiming != null
                ? transcription.GetAnalysis(forcedTiming.ToArray())
                : null;

            // Filter items that need translation
            var itemsToTranslate = transcription.Items
                .Where(item => !item.TranslatedTexts.Any(t => t.Id == translation.Id))
                .ToArray();

            var currentIndex = 0;
            int requestNumber = 1;
            string ongoingContext = null;
            string currentContext = null;

            while (currentIndex < itemsToTranslate.Length)
            {
                currentIndex = Math.Max(0, currentIndex - this.OverlapItemsInRequest);
                var itemsForRequest = itemsToTranslate
                    .Skip(currentIndex)
                    .Take(this.MaxItemsInRequest)
                    .ToArray();

                var messages = new List<dynamic>();

                // Add system prompt
                if (Options.SystemPrompt != null)
                {
                    messages.Add(new
                    {
                        role = "system",
                        content = Options.SystemPrompt.GetFinalText(transcription.Language, translation.Language)
                    });
                }

                var userContent = new StringBuilder();

                // Add previous items for context if configured
                if (this.IncludePreviousItems > 0 && currentIndex > 0)
                {
                    // TODO include text translated before
                    var startIdx = Math.Max(0, currentIndex - this.IncludePreviousItems);
                    var previousItems = itemsToTranslate.Skip(startIdx).Take(currentIndex - startIdx);

                    userContent.AppendLine($"For context, here what was said before in the scene:");
                    foreach (var item in previousItems)
                    {
                        userContent.AppendLine("   " + item.Text);
                    }
                    userContent.AppendLine();
                }

                // Add user prompt
                if (requestNumber > 1 && Options.OtherUserPrompt != null)
                {
                    userContent.AppendLine(Options.OtherUserPrompt.GetFinalText(
                        transcription.Language, translation.Language));
                }
                else if (Options.FirstUserPrompt != null)
                {
                    userContent.AppendLine(Options.FirstUserPrompt.GetFinalText(
                        transcription.Language, translation.Language));
                }

                if (userContent.Length > 0)
                {
                    userContent.AppendLine();
                }

                // Build items array with metadata
                var itemsData = itemsForRequest.Select((item, index) =>
                {
                    // Get metadata from forced timing
                    var metadata = this.Options.CreateMetadata(
                        forcedTiming,
                        item.StartTime,
                        item.EndTime,
                        ref ongoingContext);

                    // Track context changes
                    var previousContext = currentContext;
                    currentContext = forcedTiming?.GetContextAt(item.StartTime);

                    // Always include the original text
                    metadata["Original"] = item.Text;

                    // Add parts if available
                    if (Options.IncludeParts && analysis != null)
                    {
                        if (analysis.TranscribedTextWithOverlapTimings.TryGetValue(item, out var parts) && parts?.Length > 1)
                        {
                            metadata["Parts"] = parts.Select(p => p.WordsText).ToArray();
                        }
                    }

                    // Add previous translation if exists
                    var previousTranslation = item.TranslatedTexts.FirstOrDefault(t => t.Id == this.PreviousTranslationId);
                    if (!string.IsNullOrWhiteSpace(previousTranslation?.Text))
                    {
                        metadata["PreviousTranslation"] = previousTranslation.Text;
                    }

                    return metadata;
                }).ToArray();

                userContent.AppendLine(JsonConvert.SerializeObject(
                    itemsData,
                    Formatting.Indented,
                    new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));

                messages.Add(new
                {
                    role = "user",
                    content = userContent.ToString()
                });

                yield return new AIRequestForTranslation(
                    $"{transcription.Id}-{translation.Id}",
                    $"{currentIndex}/{itemsToTranslate.Length}",
                    requestNumber++,
                    messages,
                    transcription,
                    translation,
                    itemsForRequest);

                currentIndex += itemsForRequest.Length;
                ongoingContext = currentContext ?? ongoingContext;
            }
        }

        private int HandlePreviousFiles(
            SubtitleGeneratorContext context,
            Transcription transcription,
            Translation translation)
        {
            var nbErrors = 0;
            var patternSuffix = "-\\d+\\.txt";

            foreach (var fullpath in Directory.GetFiles(
                PathExtension.SafeGetDirectoryName(context.CurrentBaseFilePath),
                "*.*"))
            {
                var filename = Path.GetFileName(fullpath);
                if (Regex.IsMatch(
                    filename,
                    $"^" + Regex.Escape($"{Path.GetFileName(context.CurrentBaseFilePath)}.TODO-{transcription.Id}-{translation.Id}") + $"{patternSuffix}$",
                    RegexOptions.IgnoreCase))
                {
                    var response = File.ReadAllText(fullpath);
                    context.SoftDelete(fullpath);

                    try
                    {
                        context.WriteInfo($"        Analysing existing file '{filename}'...");
                        var nbAdded = AIRequestForTranslation.ParseAndAddTranslations(transcription, translation, response);
                        context.WriteInfo($"        Finished:");
                        context.WriteInfo($"            Nb translations added: {nbAdded}");
                        context.CurrentWipsub.Save();
                    }
                    catch (AIEngineException ex)
                    {
                        nbErrors++;
                        File.WriteAllText(fullpath, $"{ex.Message.Replace("[", "(").Replace("]", ")")}\n\n{ex.PartiallyFixedResponse}", Encoding.UTF8);
                        context.WriteInfo($"Error while parsing file '{filename}':{ex.Message}");
                        context.AddUserTodo($"Manually fix the following error in file '{filename}':\n{ex.Message}");
                    }
                }
            }

            return nbErrors;
        }
    }
}