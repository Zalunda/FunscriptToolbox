using FunscriptToolbox.SubtitlesVerbsV2.Transcriptions;
using Newtonsoft.Json;
using System.Linq;
using FunscriptToolbox.SubtitlesVerbV2;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbsV2.Translations
{
    public abstract class AIMessagesHandler
    {
        [JsonProperty(Order = 1)]
        public AIPrompt SystemPrompt { get; set; } = null;
        [JsonProperty(Order = 2)]
        public AIPrompt UserPrompt { get; set; } = null;

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
                    return new ItemForAI(item, currentContext == previousContext ? null : currentContext);
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
            public dynamic Data { get; }
            public ItemForAI[] Items { get; }
            public string FullPrompt { get; }

            public RequestForAIService(
                int requestNumber, 
                string toolAction,
                dynamic data, 
                ItemForAI[] items)
            {
                Number = requestNumber;
                ToolAction = toolAction;
                Data = data;
                Items = items;

                var fullpromptBuilder = new StringBuilder();
                foreach (var message in data.messages)
                {
                    fullpromptBuilder.AppendLine(message.content);
                }
                FullPrompt = fullpromptBuilder.ToString();
            }
        }
    }
}