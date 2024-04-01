﻿using FunscriptToolbox.SubtitlesVerbsV2.Transcriptions;
using Newtonsoft.Json;
using System.Linq;
using FunscriptToolbox.SubtitlesVerbV2;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbsV2.Translations
{
    public abstract class AIMessagesHandler
    {
        public const string TranscriptionLanguageToken = "[TranscriptionLanguage]";
        public const string TranslationLanguageToken = "[TranslationLanguage]";

        [JsonProperty(Order = 1)]
        public string[] SystemPrompt { get; set; } = null;
        [JsonProperty(Order = 2)]
        public string[] UserPrompt { get; set; } = null;

        public ItemForAI[] GetAllItems(
            Transcription transcription,
            SubtitleForcedTimingCollection subtitlesForcedTiming,
            string translationId)
        {
            string currentContext = null;
            return transcription
                .Items
                .Where(t => t.TranslatedTexts.FirstOrDefault(tt => tt.Id == translationId)?.Text == null)
                .Select(item =>
                {
                    var previousContext = currentContext;
                    currentContext = subtitlesForcedTiming.GetContextAt(item.StartTime);
                    return new ItemForAI(item, currentContext == previousContext ? null : currentContext);
                }
                ).ToArray();
        }

        public abstract IEnumerable<RequestForAIService> CreateRequests(
            string translationId,
            ItemForAI[] items,
            Language transcribedLanguage,
            Language translatedLanguage);

        public abstract void HandleResponse(
            string translationId,
            ItemForAI[] items,
            string responseReceived);

        protected string ConvertPromptLinesToPrompt(
            string[] promptLines,
            Language transcribedLanguage,
            Language translatedLanguage)
        {
            return string.Join("\n", promptLines)
                .Replace(TranscriptionLanguageToken, transcribedLanguage.LongName)
                .Replace(TranslationLanguageToken, translatedLanguage.LongName);
        }

        public class ItemForAI
        {
            [JsonIgnore]
            public TranscribedText Tag { get; }


            [JsonProperty(Order = 1, NullValueHandling = NullValueHandling.Ignore)]
            public string Context { get; set; }

            [JsonProperty(Order = 2)]
            public string StartTime { get; set; }
            [JsonProperty(Order = 3)]
            public string Original { get; set; }

            public ItemForAI(TranscribedText tag, string context)
            {
                this.Tag = tag;
                this.StartTime = tag.StartTime.TotalSeconds.ToString("F1");
                this.Original = tag.Text;
                this.Context = context;
            }
        }

        public class RequestForAIService
        {
            public int Number { get; }
            public dynamic Data { get; }
            public string FullPrompt { get; }
            public ItemForAI[] Items { get; }

            public RequestForAIService(
                int requestNumber, 
                dynamic data, 
                string fullPrompt,
                ItemForAI[] items)
            {
                Number = requestNumber;
                Data = data;
                FullPrompt = fullPrompt;
                Items = items;
            }
        }
    }
}