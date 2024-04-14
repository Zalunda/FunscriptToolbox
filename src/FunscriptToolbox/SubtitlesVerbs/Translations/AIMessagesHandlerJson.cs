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
        public int OverlapItemsInRequest { get; set; } = 0;
        [JsonProperty(Order = 12)]
        public bool IncludeStartTime { get; set; } = true;
        // [JsonProperty(Order = 13)]
        // TODO but not easy. => public bool IncludeDuration { get; set; } = true;
        [JsonProperty(Order = 14)]
        public bool IncludeContext { get; set; } = true;
        [JsonProperty(Order = 15)]
        public bool IncludeTalker { get; set; } = true;

        public override IEnumerable<RequestForAIService> CreateRequests(
            Transcription transcription,
            Translation translation,
            ItemForAICollection items)
        {
            var itemsWithoutTranslations = items.ItemsWithoutTranslation(translation);

            var currentIndex = 0;
            int requestNumber = 1;
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
                            f => new {
                                Context = this.IncludeContext ? f.Context : null,
                                Talker = this.IncludeTalker ? f.Talker : null,
                                StartTime = this.IncludeStartTime ? f.StartTime : null,
                                f.Original
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
                // Remove everything before the first '['
                var indexOfFirstBracket = json.IndexOf('[');
                if (indexOfFirstBracket < 0)
                {
                    var indexOfFirstCurlyBrace = json.IndexOf('{');
                    json = (indexOfFirstCurlyBrace < 0)
                        ? "[" + json.Substring(indexOfFirstCurlyBrace)
                        : "[" + json;
                }
                else
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