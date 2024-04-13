using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbsV2.Transcriptions;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbsV2.Outputs
{

    internal class SubtitleOutputCostReport : SubtitleOutput
    {
        public SubtitleOutputCostReport()
        {

        }

        public override bool NeedSubtitleForcedTimings => false;

        [JsonProperty(Order = 10)]
        public string FileSuffix { get; set; } = null;
        [JsonProperty(Order = 11)]
        public bool OutputToConsole { get; set; } = true;
        [JsonProperty(Order = 12)]
        public bool IsGlobalTranscriptionReport { get; set; } = true;
        [JsonProperty(Order = 13)]
        public bool IsGlobalTranslationReport { get; set; } = false;

        public override void CreateOutput(
            SubtitleGeneratorContext context,
            WorkInProgressSubtitles wipsub)
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
                    TotalNbAudios = g.Sum(c => c.NbAudios),
                    TotalTranscriptionDuration = g.Sum(c => c.TranscriptionDuration)
                });
            foreach (var task in transcriptionCosts)
            {
                yield return $"  Transcription Costs ({task.TaskName})";
                yield return $"    Total Transcription Time: {task.TotalTranscriptionTime}";
                yield return $"    Total Number of Calls: {task.TotalNbCalls}";
                yield return $"    Total Number of Audios: {task.TotalNbAudios}";
                yield return $"    Total Transcription Duration: {task.TotalTranscriptionDuration}";
                yield return string.Empty;
            }

            var translationCostsByTask = transcriptions
                    .SelectMany(t => t.Translations)
                    .SelectMany(tr => tr.Costs)
                    .GroupBy(c => IsGlobalTranslationReport ? "Global" : c.TaskName)
                    .Select(g => new
                    {
                        TaskName = g.Key,
                        TotalTranslationTime = g.Sum(c => c.TimeTaken),
                        TotalNbCalls = g.Count(),
                        TotalNbTexts = g.Sum(c => c.NbTexts),
                        TotalNbInputTokens = g.Sum(c => c.NbInputTokens ?? 0),
                        TotalNbOutputTokens = g.Sum(c => c.NbOutputTokens ?? 0)
                    });

            foreach (var task in translationCostsByTask)
            {
                yield return $"  Translation Costs ({task.TaskName})";
                yield return $"    Total Translation Time: {task.TotalTranslationTime}";
                yield return $"    Total Number of Calls: {task.TotalNbCalls}";
                yield return $"    Total Number of Texts: {task.TotalNbTexts}";
                yield return $"    Total Number of Input Tokens: {task.TotalNbInputTokens}";
                yield return $"    Total Number of Output Tokens: {task.TotalNbOutputTokens}";
                yield return string.Empty;
            }
        }
    }
}
