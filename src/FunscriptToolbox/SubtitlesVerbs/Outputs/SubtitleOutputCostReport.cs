using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.Transcriptions;
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

        public override string Description => this.FileSuffix == null ? base.Description : $"{base.Description}: {this.FileSuffix}";

        [JsonProperty(Order = 10)]
        public string FileSuffix { get; set; } = null;
        [JsonProperty(Order = 11)]
        public bool OutputToConsole { get; set; } = true;
        [JsonProperty(Order = 12)]
        public bool IsGlobalTranscriptionReport { get; set; } = false;
        [JsonProperty(Order = 13)]
        public bool IsGlobalTranslationReport { get; set; } = false;

        public override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context, 
            out string reason)
        {
            reason = null;
            return true;
        }

        public override void CreateOutput(
            SubtitleGeneratorContext context)
        {
            StreamWriter fileWriter;
            if (this.FileSuffix == null)
            {
                fileWriter = StreamWriter.Null;
            }
            else
            {
                var filename = context.CurrentBaseFilePath + this.FileSuffix;
                context.SoftDelete(filename);
                fileWriter = new StreamWriter(filename, false, Encoding.UTF8);
            }

            try
            {
                foreach (var line in GenerateConsolidatedReport(context.CurrentWipsub.Transcriptions))
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
            IEnumerable<Transcription> transcriptions)
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

            var translationCostsByTask = transcriptions
                    .SelectMany(t => t.Translations)
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
