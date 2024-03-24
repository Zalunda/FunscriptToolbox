using FunscriptToolbox.SubtitlesVerbsV2.Transcriptions;
using Newtonsoft.Json;
using System.Text;
using System;
using System.Linq;
using FunscriptToolbox.SubtitlesVerbV2;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbsV2.Translations
{
    public abstract class AITranslator : Translator 
    {
        public const string TranscriptionLanguageToken = "[TranscriptionLanguage]";
        public const string TranslationLanguageToken = "[TranslationLanguage]";

        public AITranslator(
            string translationId)
            : base(translationId)
        {
        }

        public int MaxItemsInRequest { get; set; } = 20;
        public int OverlapItemsInRequest { get; set; } = 5;
        public string[] SystemPrompt { get; set; } = null;
        public string[] UserPrompt { get; set; } = null;

        protected class ItemForAI
        {
            [JsonIgnore]
            public TranscribedText Tag { get; }

            public string StartTime { get; set; }
            public string Original { get; set; }

            public ItemForAI(TranscribedText tag)
            {
                this.Tag = tag;
                this.StartTime = tag.StartTime.TotalSeconds.ToString("F1");
                this.Original = tag.Text;
            }
        }

        protected class RequestForAIService
        {
            public int Number { get; }
            public string Content { get; }
            public ItemForAI[] Items { get; }

            public RequestForAIService(int requestNumber, string content, ItemForAI[] items)
            {
                Number = requestNumber;
                Content = content;
                Items = items;
            }
        }

        protected ItemForAI[] GetAllItems(
            Transcription transcription)
        {
            return transcription
                .Items
                .Select(item => new ItemForAI(item))
                .ToArray();
        }

        protected IEnumerable<RequestForAIService> CreateRequests(
            ItemForAI[] items,
            Language transcribedLanguage,
            Language translatedLanguage)
        {
            // See if some text have already been translated.
            var currentIndex = 0;
            while (items[currentIndex].Tag.TranslatedTexts
                    .FirstOrDefault(t => t.Id == this.TranslationId)?.Text != null)
            {
                currentIndex++;
            }

            int requestNumber = 1;
            while (currentIndex < items.Length)
            {
                currentIndex = Math.Max(0, currentIndex - this.OverlapItemsInRequest);
                var itemsForRequest = items
                    .Skip(currentIndex)
                    .Take(this.MaxItemsInRequest)
                    .ToArray();

                var request = new StringBuilder();
                if (this.UserPrompt != null)
                {
                    request.AppendLine(
                        ConvertPromptLinesToPrompt(
                            this.UserPrompt,
                            transcribedLanguage,
                            translatedLanguage));
                    request.AppendLine();
                }
                request.AppendLine(
                    JsonConvert.SerializeObject(
                        itemsForRequest, 
                        Formatting.Indented));

                yield return new RequestForAIService(
                    requestNumber++,
                    request.ToString(),
                    itemsForRequest);
                currentIndex += itemsForRequest.Length; 
            }
        }

        protected string ConvertPromptLinesToPrompt(
            string[] promptLines,
            Language transcribedLanguage,
            Language translatedLanguage)
        {
            return string.Join("\n", promptLines)
                .Replace(TranscriptionLanguageToken, transcribedLanguage.LongName)
                .Replace(TranslationLanguageToken, translatedLanguage.LongName);
        }

        protected void HandleResponse(
            RequestForAIService request,
            string responseReceived)
        {
            var result = ParseAndFixJson(responseReceived);
            foreach (var item in result)
            {
                var startTime = (string)item.Start;
                var original = (string)item.Original;
                var translation = (string)item.Translation;
                var originalItem = request.Items.FirstOrDefault(f => f.StartTime == startTime && f.Original == original)
                    ?? request.Items.FirstOrDefault(f => f.StartTime == startTime)
                    ?? request.Items.FirstOrDefault(f => f.Original == original);
                if (originalItem != null)
                {
                    originalItem.Tag.TranslatedTexts.Add(
                        new TranslatedText(
                            this.TranslationId, 
                            translation));
                }
                else
                {
                    // TODO: Log SerializeObject(item) could not be matched to an items.
                }
            }
            foreach (var item in request.Items)
            {
                if (item.Tag.TranslatedTexts
                    .FirstOrDefault(t => t.Id == this.TranslationId)?.Text == null)
                {
                    // TODO: Log SerializeObject(item) was not filled.
                }
            }
        }

        private dynamic ParseAndFixJson(string json)
        {
            var indexOfFirstBracket = json.IndexOf('[');
            if (indexOfFirstBracket > 0)
            {
                json = json.Substring(indexOfFirstBracket);
            }
            var indexOfLastBracket = json.LastIndexOf(']');
            if (indexOfLastBracket >= 0 && indexOfLastBracket < json.Length)
            {
                json = json.Substring(0, indexOfLastBracket + 1);
            }
            else
            {
                json = json + "]";
            }
            while (true)
            {
                try
                {
                    var result = JsonConvert.DeserializeObject<dynamic>(json);
                    return result;
                }
                catch(Exception ex)
                {
                    throw;
                    // TODO Try to fix
                }
            }
        }
    }
}