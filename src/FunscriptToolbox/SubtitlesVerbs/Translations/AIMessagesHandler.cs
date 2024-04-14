﻿using FunscriptToolbox.SubtitlesVerbs.Transcriptions;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbs.Translations
{
    public abstract class AIMessagesHandler
    {
        [JsonProperty(Order = 1)]
        public AIPrompt SystemPrompt { get; set; } = null;
        [JsonProperty(Order = 2)]
        public AIPrompt FirstUserPrompt { get; set; } = null;
        [JsonProperty(Order = 3)]
        public AIPrompt OtherUserPrompt { get; set; } = null;

        public ItemForAICollection GetAllItems(
            Transcription transcription,
            SubtitleForcedTimingCollection subtitlesForcedTiming)
        {
            string currentContext = null;
            return new ItemForAICollection(
                transcription
                .Items
                .Select(item =>
                {
                    var previousContext = currentContext;
                    currentContext = subtitlesForcedTiming.GetContextAt(item.StartTime);
                    return new ItemForAI(
                        item, 
                        currentContext == previousContext ? null : currentContext,
                        subtitlesForcedTiming?.GetTalkerAt(item.StartTime, item.EndTime));
                }));
        }

        public abstract IEnumerable<RequestForAIService> CreateRequests(
            Transcription transcription,
            Translation translation,
            ItemForAICollection items);

        public abstract int HandleResponse(
            Translation translation,
            ItemForAICollection allItems,
            string responseReceived,
            ItemForAI[] requestItems = null);

        public class ItemForAI
        {
            [JsonIgnore]
            public TranscribedText Tag { get; }


            [JsonProperty(Order = 1, NullValueHandling = NullValueHandling.Ignore)]
            public string Context { get; set; }
            [JsonProperty(Order = 2, NullValueHandling = NullValueHandling.Ignore)]
            public string Talker { get; set; }
            [JsonProperty(Order = 3)]
            public string StartTime { get; set; }
            [JsonProperty(Order = 4)]
            public string Original { get; set; }

            public ItemForAI(TranscribedText tag, string context, string talker)
            {
                this.Tag = tag;
                this.StartTime = tag.StartTime.TotalSeconds.ToString("F1");
                this.Original = tag.Text;
                this.Context = context;
                this.Talker = talker;
            }
        }

        public class ItemForAICollection : ReadOnlyCollection<ItemForAI>
        {
            public ItemForAICollection(IEnumerable<ItemForAI> list)
                : base(list.ToArray())
            {
            }

            public ItemForAI[] ItemsWithoutTranslation(
                Translation translation)
            {
                return this
                    .Where(t => t.Tag.TranslatedTexts.FirstOrDefault(tt => tt.Id == translation.Id)?.Text == null)
                    .ToArray();
            }

            public bool IsFinished(Translation translation)
            {
                return !this
                    .Any(t => t.Tag.TranslatedTexts.FirstOrDefault(tt => tt.Id == translation.Id)?.Text == null);
            }

        }

        public class RequestForAIService
        {
            public int Number { get; }
            public string ToolAction { get; }
            public dynamic Body { get; }
            public ItemForAI[] Items { get; }
            public string FullPrompt { get; }

            public RequestForAIService(
                int requestNumber, 
                string toolAction,
                dynamic body, 
                ItemForAI[] items)
            {
                Number = requestNumber;
                ToolAction = toolAction;
                Body = body;
                Items = items;

                var fullpromptBuilder = new StringBuilder();
                foreach (var message in body.messages)
                {
                    fullpromptBuilder.AppendLine(message.content);
                }
                FullPrompt = fullpromptBuilder.ToString();
            }
        }
    }
}