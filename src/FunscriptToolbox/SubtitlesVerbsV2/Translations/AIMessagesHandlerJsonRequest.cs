using Newtonsoft.Json;
using System.Text;
using System;
using System.Linq;
using FunscriptToolbox.SubtitlesVerbV2;
using System.Collections.Generic;
using System.Dynamic;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerbsV2.Translations
{
    public class AIMessagesHandlerJsonRequest : AIMessagesHandler
    {
        public AIMessagesHandlerJsonRequest() 
        { 
        }

        [JsonProperty(Order = 10)]
        public int MaxItemsInRequest { get; set; } = 20;
        [JsonProperty(Order = 11)]
        public int OverlapItemsInRequest { get; set; } = 5;

        public override IEnumerable<RequestForAIService> CreateRequests(
            string translationId,
            ItemForAI[] items,
            Language transcribedLanguage,
            Language translatedLanguage)
        {
            var currentIndex = 0;
            int requestNumber = 1;
            while (currentIndex < items.Length)
            {
                currentIndex = Math.Max(0, currentIndex - this.OverlapItemsInRequest);
                var itemsForRequest = items
                    .Skip(currentIndex)
                    .Take(this.MaxItemsInRequest)
                    .ToArray();

                var fullPrompt = new StringBuilder();

                var messages = new List<dynamic>();
                if (SystemPrompt != null)
                {
                    var systemContent = SystemPrompt.GetFinalText(
                                transcribedLanguage,
                                translatedLanguage);
                    messages.Add(
                        new
                        {
                            role = "system",
                            content = systemContent
                        });
                    fullPrompt.AppendLine(systemContent.ToString());
                }

                var userContent = new StringBuilder();
                if (this.UserPrompt != null)
                {
                    userContent.AppendLine(
                        this.UserPrompt.GetFinalText(
                            transcribedLanguage,
                            translatedLanguage));
                    userContent.AppendLine();
                }
                userContent.AppendLine(
                    JsonConvert.SerializeObject(
                        itemsForRequest,
                        Formatting.Indented));
                messages.Add(
                    new
                    {
                        role = "user",
                        content = userContent.ToString()
                    });
                fullPrompt.AppendLine(userContent.ToString());

                dynamic data = new ExpandoObject();
                data.messages = messages;

                yield return new RequestForAIService(
                    requestNumber++,
                    data,
                    fullPrompt.ToString(),
                    itemsForRequest); ;
                currentIndex += itemsForRequest.Length;
            }
        }

        public override int HandleResponse(
            string translationId,
            ItemForAI[] items,
            string responseReceived)
        {
            var nbTranslationsAdded = 0;
            var result = ParseAndFixJson(responseReceived);
            foreach (var item in result)
            {
                var startTime = (string)item.StartTime;
                var original = (string)item.Original;
                var translation = (string)item.Translation;
                if (translation != null)
                {
                    var originalItem = items.FirstOrDefault(f => f.StartTime == startTime && f.Original == original)
                        ?? items.FirstOrDefault(f => f.StartTime == startTime)
                        ?? items.FirstOrDefault(f => f.Original == original);
                    if (originalItem != null)
                    {
                        nbTranslationsAdded++;
                        originalItem.Tag.TranslatedTexts.Add(
                            new TranslatedText(
                                translationId,
                                translation));
                    }
                    else
                    {
                        var k = 0;
                        // TODO: Log SerializeObject(item) could not be matched to an items.
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
                if (indexOfFirstBracket > 0)
                {
                    json = json.Substring(indexOfFirstBracket);
                }

                // Remove everything after the last ']', add a ']' at the end, if missing.
                var indexOfLastBracket = json.LastIndexOf(']');
                if (indexOfLastBracket >= 0 && indexOfLastBracket < json.Length)
                {
                    json = json.Substring(0, indexOfLastBracket + 1);
                }
                else
                {
                    json += "]";
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