using System;
using System.Collections.Generic;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class AIRequest
    {
        public DateTime ProcessStartTime { get; }
        public int Number { get; }
        public string TaskId { get; }
        public TimedItemWithMetadata[] ItemsIncluded { get; }
        public AIRequestPart[] SystemParts { get; }
        public AIRequestPart[] UserParts { get; }
        public string MetadataAlwaysProduced { get; }
        public string UpdateMessage { get; }
        public TimeSpan StartOffset { get; }

        public string FullPrompt { get; }

        public AIRequest(
            DateTime processStartTime,
            int requestNumber,
            string taskId,
            TimedItemWithMetadata[] ItemsIncluded,
            List<AIRequestPart> systemParts,
            List<AIRequestPart> userParts,
            string metadataAlwaysProduced,
            string updateMessage,
            TimeSpan? startOffset = null)
        {
            this.ProcessStartTime = processStartTime;
            this.Number = requestNumber;
            this.TaskId = taskId;
            this.ItemsIncluded = ItemsIncluded;
            this.SystemParts = systemParts?.ToArray() ?? Array.Empty<AIRequestPart>();
            this.UserParts = userParts?.ToArray() ?? Array.Empty<AIRequestPart>();
            this.MetadataAlwaysProduced = metadataAlwaysProduced;
            this.UpdateMessage = updateMessage;
            this.StartOffset = startOffset ?? TimeSpan.Zero;

            var fullpromptBuilder = new StringBuilder();
            fullpromptBuilder.AppendLine("******* System ********************************");
            foreach (var item in this.SystemParts)
            {
                fullpromptBuilder.Append(item.ForSimplifiedFullPrompt());
            }
            fullpromptBuilder.AppendLine("\n\n******* User **********************************");
            foreach (var item in this.UserParts)
            {
                fullpromptBuilder.Append(item.ForSimplifiedFullPrompt());
            }
            FullPrompt = fullpromptBuilder.ToString();
        }

        public virtual string GetFilenamePattern(string baseFilePath)
        {
            return $"{baseFilePath}.TODO_{this.TaskId}_{this.Number:D04}.txt";
        }

        public virtual string GetVerbosePrefix()
        {
            return $"{TaskId}_{Number:D04}";
        }
    }
}