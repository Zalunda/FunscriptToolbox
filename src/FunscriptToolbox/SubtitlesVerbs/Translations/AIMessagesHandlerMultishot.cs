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
    public class AIMessagesHandlerMultishot : AIMessagesHandler
    {
        public AIMessagesHandlerMultishot() 
        { 
        }

        [JsonProperty(Order = 10)]
        public int MaxPreviousShot { get; set; } = 20;
        [JsonProperty(Order = 11)]
        public bool IncludeExamples { get; set; } = true;
        [JsonProperty(Order = 12)]
        public bool IncludeContext { get; set; } = true;
        [JsonProperty(Order = 13)]
        public bool IncludeTalker { get; set; } = true;
        [JsonProperty(Order = 14)]
        public string ContextTextFormat { get; set; } = "Context{{<text>}}";
        [JsonProperty(Order = 15)]
        public string TalkerTextFormat { get; set; } = "Talker{{<text>}}";
        [JsonProperty(Order = 16)]
        public string OriginalTextFormat { get; set; } = "Original{{<text>}}";
        [JsonProperty(Order = 17)]
        public string TranslationTextFormat { get; set; } = "Translation{{<text>}}";
        [JsonProperty(Order = 18)]
        public string TranslationTextExtractionRegex { get; set; } = "{+(?<text>[^}]*)";

        internal override bool IsReadyToStart(
            Transcription transcription,
            out string reason)
        {
            reason = null;
            return true;
        }

        public override IEnumerable<RequestForAIService> CreateRequests(
            Transcription transcription,
            Translation translation,
            ItemForAICollection items)
        {
            int requestNumber = 1;
            for (int currentIndex = 0; currentIndex < items.Count; currentIndex++)
            {
                var currentItem = items[currentIndex];
                var currentTranscribedText = currentItem.Tag;
                var alreadyTranslated = currentTranscribedText.TranslatedTexts
                    .FirstOrDefault(t => t.Id == translation.Id)?.Text;
                if (alreadyTranslated != null)
                {
                    continue;
                }

                var firstIndex = Math.Max(0, currentIndex - this.MaxPreviousShot);
                var itemsForThisRequest = items
                    .Take(currentIndex + 1)
                    .Skip(firstIndex)
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

                if (this.IncludeExamples)
                {
                    foreach (var example in translation.GetTranslationExamples(
                        transcription.Language, 
                        this.MaxPreviousShot - itemsForThisRequest.Length))
                    {
                        messages.Add(new { role = "user", content = this.OriginalTextFormat.Replace("<text>", example.Original) });
                        messages.Add(new { role = "assistant", content = this.TranslationTextFormat.Replace("<text>", example.Translation) });
                    }
                }

                var currentContext = items
                    .Take(currentIndex - this.MaxPreviousShot)
                    .LastOrDefault(f => f.Context != null)
                    ?.Context;
                foreach (var item in itemsForThisRequest)
                {
                    var userContent = new StringBuilder();
                    if (item.Context != currentContext)
                    {
                        currentContext = item.Context;
                    }
                    if (IncludeContext && currentContext != null)
                    {
                        userContent.AppendLine(this.ContextTextFormat.Replace("<text>", currentContext));
                    }
                    if (IncludeTalker && item.Talker != null)
                    {
                        userContent.AppendLine(this.TalkerTextFormat.Replace("<text>", item.Talker));
                    }
                    userContent.AppendLine(this.OriginalTextFormat.Replace("<text>", item.Original));
                    messages.Add(new { role = "user", content = userContent.ToString() });

                    var itemTranslation = item.Tag.TranslatedTexts
                        .FirstOrDefault(t => t.Id == translation.Id)?.Text;
                    if (itemTranslation != null)
                    {
                        messages.Add(new { role = "assistant", content = this.TranslationTextFormat.Replace("<text>", itemTranslation) });
                    }
                }

                dynamic data = new ExpandoObject();
                data.messages = messages;

                yield return new RequestForAIService(
                    requestNumber++,
                    $"{currentIndex + 1}/{items.Count}",
                    data,
                    new[] { currentItem });
            }
        }

        public override int HandleResponse(
            Translation translation,
            ItemForAICollection allItems,
            string responseReceived,
            ItemForAI[] requestItems)
        {
            if (requestItems == null)
            {
                return 0;
            }
            else
            {
                var match = Regex.Match(responseReceived, this.TranslationTextExtractionRegex, RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    requestItems.First().Tag.TranslatedTexts.Add(
                                    new TranslatedText(
                                        translation.Id,
                                        match.Groups["text"].Value));
                    return 1;
                }
                else
                {
                    return 0;
                }
            }
        }
    }
}