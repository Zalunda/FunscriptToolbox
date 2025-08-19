using FunscriptToolbox.SubtitlesVerbs.Translations;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerbs
{
    public abstract class AIRequest
    {
        public string TaskId { get; }
        public string ToolAction { get; }
        public int Number { get; }
        public List<dynamic> Messages { get; }
        public string FullPrompt { get; }

        // In AIRequest.cs, update the FullPrompt generation to handle multimodal content:

        protected AIRequest(
            string taskId,
            string toolAction,
            int requestNumber,
            List<dynamic> messages)
        {
            TaskId = taskId;
            ToolAction = toolAction;
            Number = requestNumber;
            Messages = messages;

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
                        }
                    }
                }
            }
            FullPrompt = fullpromptBuilder.ToString();
        }

        public abstract void HandleResponse(
            SubtitleGeneratorContext context,
            string taskName,
            TimeSpan timeTaken,
            string responseReceived,
            int? promptTokens = null,
            int? completionTokens = null,
            int? totalTokens = null);

        public virtual string GetFilenamePattern(string baseFilePath)
        {
            return $"{baseFilePath}.TODO-{this.TaskId}-{this.Number:D04}.txt";
        }

        public virtual string GetVerbosePrefix()
        {
            return $"{TaskId}-{Number:D04}";
        }

        public abstract string NbItemsString();

        protected static dynamic ParseAndFixJson(string json)
        {
            try
            {
                json = Regex.Replace(json, @"\<think\>.*\<\/think\>", string.Empty, RegexOptions.Singleline);
                json = Regex.Replace(json, @"^\s*>.*$", string.Empty, RegexOptions.Multiline);

                var indexOfFirstBracket = json.IndexOf('[');
                if (indexOfFirstBracket >= 0)
                {
                    json = json.Substring(indexOfFirstBracket);
                }

                var indexOfLastBracket = json.LastIndexOf(']');
                if (indexOfLastBracket < 0)
                {
                    json += "]";
                }
                else
                {
                    json = json.Substring(0, indexOfLastBracket + 1);
                }

                json = Regex.Replace(json, @"(""(Original|StartTime)"": ""[^""]*""(?!\s*,))", "$1,");
                json = Regex.Replace(json, @"(})(\s*{)", "$1,$2");

                return JsonConvert.DeserializeObject<dynamic>(json);
            }
            catch (Exception ex)
            {
                throw new AIEngineException(ex, json);
            }
        }
    }
}