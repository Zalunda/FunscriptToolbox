using FunscriptToolbox.SubtitlesVerbs.Transcriptions;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerbs.Translations
{
    public class AIMessagesHandlerJson : AIMessagesHandler
    {
        public AIMessagesHandlerJson()
        {
        }


        [JsonProperty(Order = 10)]
        public int MaxItemsInRequest { get; set; } = 20;
        [JsonProperty(Order = 11)]
        public int IncludePreviousItems { get; set; } = 0;
        [JsonProperty(Order = 12)]
        public int OverlapItemsInRequest { get; set; } = 0;
        [JsonProperty(Order = 13)]
        public bool IncludeStartTime { get; set; } = true;
        [JsonProperty(Order = 14)]
        public bool IncludeEndTime { get; set; } = false;
        [JsonProperty(Order = 15)]
        public bool IncludeContext { get; set; } = true;
        [JsonProperty(Order = 16)]
        public bool IncludeTalker { get; set; } = true;
        [JsonProperty(Order = 17)]
        public bool IncludeParts { get; set; } = false;
        [JsonProperty(Order = 18)]
        public string PreviousTranslationId { get; set; }

        internal override bool IsReadyToStart(
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

        public override IEnumerable<RequestForAIService> CreateRequests(
            Transcription transcription,
            Translation translation,
            ItemForAICollection items)
        {
            var itemsWithoutTranslations = items.ItemsWithoutTranslation(translation);

            var currentIndex = 0;
            int requestNumber = 1;
            string ongoingContext = itemsWithoutTranslations.FirstOrDefault()?.OngoingContext;
            while (currentIndex < itemsWithoutTranslations.Length)
            {
                currentIndex = Math.Max(0, currentIndex - this.OverlapItemsInRequest);
                var itemsForRequest = itemsWithoutTranslations
                    .Skip(currentIndex)
                    .Take(this.MaxItemsInRequest)
                    .ToArray();

                var messages = new List<dynamic>();
                if (SystemPrompt != null)
                {
                    var systemContent = SystemPrompt.GetFinalText(
                                transcription.Language,
                                translation.Language);
                    messages.Add(
                        new
                        {
                            role = "system",
                            content = systemContent
                        });
                }

                var userContent = new StringBuilder();
                if (this.IncludePreviousItems > 0)
                {
                    var previousItems = items.Take(items.IndexOf(itemsForRequest[0]) - 1).ToArray();
                    if (previousItems.Length > 0)
                    {
                        userContent.AppendLine($"For context, here what was said before in the scene:");
                        foreach (var item in previousItems.Skip(Math.Max(0, previousItems.Length - this.IncludePreviousItems)))
                        {
                            userContent.AppendLine("   " +
                                ((this.PreviousTranslationId != null) && false
                                ? item.Tag.TranslatedTexts.FirstOrDefault(f => f.Id == PreviousTranslationId)?.Text
                                : item.Original));
                        }
                        userContent.AppendLine();
                    }
                }

                if (this.OtherUserPrompt != null && requestNumber > 1)
                {
                    userContent.AppendLine(
                        this.OtherUserPrompt.GetFinalText(
                            transcription.Language,
                            translation.Language));
                    userContent.AppendLine();
                }
                else if (this.FirstUserPrompt != null)
                {
                    userContent.AppendLine(
                        this.FirstUserPrompt.GetFinalText(
                            transcription.Language,
                            translation.Language));
                    userContent.AppendLine();
                }

                userContent.AppendLine(
                    JsonConvert.SerializeObject(
                        itemsForRequest.Select(
                            (f, index) => new {
                                Context = this.IncludeContext ? f.Context : null,
                                OngoingContext = (this.IncludeContext && index == 0 && f.Context == null) ? ongoingContext : null,
                                Talker = this.IncludeTalker ? f.Talker : null,
                                StartTime = this.IncludeStartTime ? f.StartTime : null,
                                EndTime = this.IncludeEndTime ? f.EndTime : null,
                                f.Original,
                                Parts = this.IncludeParts && f.Parts?.Length > 1 ? f.Parts : null,
                                PreviousTranslation = f.Tag.TranslatedTexts.FirstOrDefault(f => f.Id == PreviousTranslationId)?.Text
                            }),
                        Formatting.Indented, 
                        new JsonSerializerSettings
                        {
                            NullValueHandling = NullValueHandling.Ignore
                        }));
                messages.Add(
                    new
                    {
                        role = "user",
                        content = userContent.ToString()
                    });

                dynamic data = new ExpandoObject();
                data.messages = messages;

                yield return new RequestForAIService(
                    requestNumber++,
                    $"{currentIndex}/{itemsWithoutTranslations.Length}",                    
                    data,
                    itemsForRequest);
                currentIndex += itemsForRequest.Length;

                ongoingContext = itemsForRequest.LastOrDefault(f => f.Context != null)?.Context ?? ongoingContext;
            }
        }

        public override int HandleResponse(
            Translation translation,
            ItemForAICollection allItems,
            string responseReceived,
            ItemForAI[] requestItems)
        {
            var nbTranslationsAdded = 0;
            var result = ParseAndFixJson(responseReceived);
            foreach (var item in result)
            {
                var startTime = (string)item.StartTime;
                var original = (string)item.Original;
                var translatedText = (string)item.Translation;
                if (translatedText != null)
                {
                    var originalItem = allItems.FirstOrDefault(f => f.StartTime == startTime && f.Original == original)
                        ?? allItems.FirstOrDefault(f => f.StartTime == startTime)
                        ?? allItems.FirstOrDefault(f => f.Original == original);
                    if (originalItem != null)
                    {
                        nbTranslationsAdded++;
                        originalItem.Tag.TranslatedTexts.Add(
                            new TranslatedText(
                                translation.Id,
                                translatedText));
                    }
                }
            }
            return nbTranslationsAdded;
        }

        private dynamic ParseAndFixJson(string json)
        {
            try
            {
                json = Regex.Replace(json, @"\<think\>.*\<\/think\>", string.Empty, RegexOptions.Singleline);

                // Remove everything before the first '['
                var indexOfFirstBracket = json.IndexOf('[');
                if (indexOfFirstBracket >= 0)
                {
                    json = json.Substring(indexOfFirstBracket);
                }

                // Remove everything after the last ']', add a ']' at the end, if missing.
                var indexOfLastBracket = json.LastIndexOf(']');
                if (indexOfLastBracket < 0)
                {
                    json += "]";
                }
                else
                {
                    json = json.Substring(0, indexOfLastBracket + 1);
                }

                // Add missing comma at the end of a field line
                json = Regex.Replace(json, @"(""(Original|StartTime)"": ""[^""]*""(?!\s*,))", "$1,");

                // Add missing comma between items
                json = Regex.Replace(json, @"(})(\s*{)", "$1,$2");

                return JsonConvert.DeserializeObject<dynamic>(json);
            }
            catch (Exception ex)
            {
                throw new AIMessagesHandlerExpection(ex, json);
            }
        }
    }
}