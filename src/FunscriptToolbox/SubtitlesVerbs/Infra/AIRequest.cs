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
        public List<dynamic> Messages { get; }
        public string MetadataAlwaysProduced { get; }
        public string UpdateMessage { get; }
        public TimeSpan StartOffset { get; }

        public string FullPrompt { get; }

        public AIRequest(
            DateTime processStartTime,
            int requestNumber,
            string taskId,
            TimedItemWithMetadata[] ItemsIncluded,
            List<dynamic> messages,
            string metadataAlwaysProduced,
            string updateMessage,
            TimeSpan? startOffset = null)
        {
            this.ProcessStartTime = processStartTime;
            this.Number = requestNumber;
            this.TaskId = taskId;
            this.ItemsIncluded = ItemsIncluded;
            this.Messages = messages;
            this.MetadataAlwaysProduced = metadataAlwaysProduced;
            this.UpdateMessage = updateMessage;
            this.StartOffset = startOffset ?? TimeSpan.Zero;

            var fullpromptBuilder = new StringBuilder();
            if (messages != null)
            {
                foreach (var message in messages)
                {
                    if (message.content is string)
                    {
                        fullpromptBuilder.AppendLine(message.content);
                    }
                    else if (message.content is Array)
                    {
                        // Handle multimodal content
                        foreach (var item in message.content)
                        {
                            if (item is BinaryDataContainer container)
                            {
                                fullpromptBuilder.Append($"[{container.DataType} data]");
                            }
                            else if (item?.type == "text")
                            {
                                fullpromptBuilder.Append(item.text ?? item.content);
                            }
                            else if (item?.type == "input_audio")
                            {
                                fullpromptBuilder.Append("[Audio data]");
                            }
                            else if (item?.type == "image_url")
                            {
                                fullpromptBuilder.Append("[Image data]");
                            }
                        }
                    }
                    fullpromptBuilder.AppendLine();
                    fullpromptBuilder.AppendLine(new string('=', 40));
                }
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