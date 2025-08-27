using System;
using System.Collections.Generic;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class AIRequest
    {
        public int Number { get; }
        public string TaskId { get; }
        public List<dynamic> Messages { get; }
        public int NbItemsToDoTotal { get; }
        public string FullPrompt { get; }

        public AIRequest(
            int requestNumber,
            string taskId,
            List<dynamic> messages,
            int nbItemsToDoTotal)
        {
            this.Number = requestNumber;
            this.TaskId = taskId;
            this.Messages = messages;
            this.NbItemsToDoTotal = nbItemsToDoTotal;

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
                            if (item?.type == "text")
                            {
                                fullpromptBuilder.AppendLine(item.text ?? item.content);
                            }
                            else if (item?.type == "input_audio")
                            {
                                fullpromptBuilder.AppendLine("[Audio data]");
                            }
                            else if (item?.type == "image_url")
                            {
                                fullpromptBuilder.AppendLine("[Image data]");
                            }
                        }
                    }
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
            return $"{TaskId}-{Number:D04}";
        }
    }
}