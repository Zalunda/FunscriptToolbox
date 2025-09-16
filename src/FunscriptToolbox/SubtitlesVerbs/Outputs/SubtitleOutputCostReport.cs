using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.Transcriptions;
using FunscriptToolbox.SubtitlesVerbs.Translations;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbs.Outputs
{

    internal class SubtitleOutputCostReport : SubtitleOutput
    {
        public SubtitleOutputCostReport()
        {

        }


        [JsonProperty(Order = 5, Required = Required.Always)]
        public string FileSuffix { get; set; }
        [JsonProperty(Order = 10)]
        public bool OutputToConsole { get; set; } = true;
        [JsonProperty(Order = 11)]
        public bool IsGlobalTranscriptionReport { get; set; } = false;
        [JsonProperty(Order = 12)]
        public bool IsGlobalTranslationReport { get; set; } = false;

        protected override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context, 
            out string reason)
        {
            reason = null;
            return true;
        }

        protected override bool IsFinished(SubtitleGeneratorContext context)
        {
            var filepath = context.WIP.BaseFilePath + this.FileSuffix;
            return File.Exists(filepath);
        }

        protected override void DoWork(
            SubtitleGeneratorContext context)
        {
            StreamWriter fileWriter;
            if (this.FileSuffix == null)
            {
                fileWriter = StreamWriter.Null;
            }
            else
            {
                var filename = context.WIP.BaseFilePath + this.FileSuffix;
                context.SoftDelete(filename);
                fileWriter = new StreamWriter(filename, false, Encoding.UTF8);
            }

            try
            {
                foreach (var line in GenerateConsolidatedReport(context.WIP.Transcriptions, context.WIP.Translations))
                {
                    fileWriter.WriteLine(line);
                    if (OutputToConsole)
                        context.WriteInfo(line);
                }
            }
            finally
            {
                fileWriter.Close();
            }
        }

        private IEnumerable<string> GenerateConsolidatedReport(
            IEnumerable<Transcription> transcriptions,
            IEnumerable<Translation> translations)
        {
            var transcriptionCosts = transcriptions
                .SelectMany(t => t.Costs)
                .GroupBy(c => IsGlobalTranscriptionReport ? "Global": c.TaskName)
                .Select(g => new
                {
                    TaskName = g.Key,
                    TotalTranscriptionTime = g.Sum(c => c.TimeTaken),
                    TotalNbCalls = g.Count(),
                    TotalNbItems = g.Sum(c => c.NbItems),
                    TotalItemsDuration = g.Sum(c => c.ItemsDuration ?? TimeSpan.Zero),
                    TotalNbPromptTokens = g.Sum(c => c.NbPromptTokens ?? 0),
                    TotalNbCompletionTokens = g.Sum(c => c.NbCompletionTokens ?? 0),
                    TotalNbTotalTokens = g.Sum(c => c.NbTotalTokens ?? 0),
                    TotalUnaccountedPromptCharacters = g.Sum(c => c.NbPromptTokens == null ? c.NbPromptCharacters : 0),
                    TotalUnaccountedCompletionsCharacters = g.Sum(c => c.NbPromptTokens == null ? c.NbCompletionCharacters : 0)
                });
            foreach (var task in transcriptionCosts)
            {
                yield return $"  Transcription Costs ({task.TaskName})";
                yield return $"    Total Transcription Time: {task.TotalTranscriptionTime}";
                yield return $"    Total Number of Calls: {task.TotalNbCalls}";
                yield return $"    Total Number of Items: {task.TotalNbItems}";
                yield return $"    Total Items Duration: {task.TotalItemsDuration}";
                yield return $"    Total Number of Prompt Tokens: {task.TotalNbPromptTokens}";
                yield return $"    Total Number of Completion Tokens: {task.TotalNbCompletionTokens}";
                yield return $"    Total Number of Total Tokens: {task.TotalNbTotalTokens}";
                yield return $"    Total Number of Prompt Characters: {task.TotalUnaccountedPromptCharacters}";
                yield return $"    Total Number of Completions Characters: {task.TotalUnaccountedCompletionsCharacters}";
                yield return string.Empty;
            }

            var translationCostsByTask = translations
                    .SelectMany(tr => tr.Costs)
                    .GroupBy(c => IsGlobalTranslationReport ? "Global" : c.TaskName)
                    .Select(g => new
                    {
                        TaskName = g.Key,
                        TotalTranscriptionTime = g.Sum(c => c.TimeTaken),
                        TotalNbCalls = g.Count(),
                        TotalNbItems = g.Sum(c => c.NbItems),
                        TotalItemsDuration = g.Sum(c => c.ItemsDuration ?? TimeSpan.Zero),
                        TotalNbPromptTokens = g.Sum(c => c.NbPromptTokens ?? 0),
                        TotalNbCompletionTokens = g.Sum(c => c.NbCompletionTokens ?? 0),
                        TotalNbTotalTokens = g.Sum(c => c.NbTotalTokens ?? 0),
                        TotalUnaccountedPromptCharacters = g.Sum(c => c.NbPromptTokens == null ? c.NbPromptCharacters : 0),
                        TotalUnaccountedCompletionsCharacters = g.Sum(c => c.NbPromptTokens == null ? c.NbCompletionCharacters : 0)
                    });

            foreach (var task in translationCostsByTask)
            {
                yield return $"  Transcription Costs ({task.TaskName})";
                yield return $"    Total Transcription Time: {task.TotalTranscriptionTime}";
                yield return $"    Total Number of Calls: {task.TotalNbCalls}";
                yield return $"    Total Number of Items: {task.TotalNbItems}";
                yield return $"    Total Items Duration: {task.TotalItemsDuration}";
                yield return $"    Total Number of Prompt Tokens: {task.TotalNbPromptTokens}";
                yield return $"    Total Number of Completion Tokens: {task.TotalNbCompletionTokens}";
                yield return $"    Total Number of Total Tokens: {task.TotalNbTotalTokens}";
                yield return $"    Total Number of Prompt Characters: {task.TotalUnaccountedPromptCharacters}";
                yield return $"    Total Number of Completions Characters: {task.TotalUnaccountedCompletionsCharacters}";
                yield return string.Empty;
            }
        }
    }
}
