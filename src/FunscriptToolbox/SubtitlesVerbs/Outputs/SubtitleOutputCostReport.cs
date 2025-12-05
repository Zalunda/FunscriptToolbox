using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.Infra;
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
            // 1. Flatten all costs into a manipulatable list with their target Group Name
            var allCosts = new List<(string GroupName, Cost Cost)>();

            if (transcriptions != null)
            {
                foreach (var t in transcriptions)
                {
                    foreach (var cost in t.Costs)
                    {
                        string name = IsGlobalTranscriptionReport ? "Global" : cost.TaskName;
                        allCosts.Add((name, cost));
                    }
                }
            }

            if (translations != null)
            {
                foreach (var t in translations)
                {
                    foreach (var cost in t.Costs)
                    {
                        string name = IsGlobalTranslationReport ? "Global" : cost.TaskName;
                        allCosts.Add((name, cost));
                    }
                }
            }

            // Helper to calculate total dollars for a list of costs
            double CalculateTotalDollars(IEnumerable<Cost> costsToSum)
            {
                double input = costsToSum.Sum(c => (c.Input.TotalTokens / 1_000_000.0) * c.Input.EstimatedCostPerMillionTokens);
                double output = costsToSum.Sum(c => (c.Output.TotalTokens / 1_000_000.0) * c.Output.EstimatedCostPerMillionTokens);
                return input + output;
            }

            // Helper to determine if a cost is purely fallback/stats based (no tokens)
            bool IsFallbackCost(Cost c)
            {
                // Check if the primary input section has the Fallback flag
                // (User stated a cost is either all fallback or all normal)
                return c.Input.Sections.Keys.Any(k => k.HasFlag(AIRequestSection.FALLBACK)) 
                    || c.Output.Sections.Keys.Any(k => k.HasFlag(AIResponseSection.FALLBACK));
            }

            // 2. Group by Task Name (Level 1)
            var taskGroups = allCosts
                .GroupBy(x => x.GroupName)
                .OrderBy(g => g.First().Cost.CreationTime);

            const string NoteForResponseWithoutTokens = " [NOTE: cost couldn't be estimated for responses without tokens]";
            var taskGlobalCost = CalculateTotalDollars(allCosts.Select( f=> f.Cost));
            var globalCostWarning = allCosts.Select(c => c.Cost).Any(c => IsFallbackCost(c)) ? NoteForResponseWithoutTokens : "";

            yield return "========================================================================";
            yield return " NOTE: All costs are estimates based on token usage (when available).";
            yield return "       Please refer to your vendor billing for the actual final charges.";
            yield return "========================================================================";
            yield return string.Empty;
            yield return $"Global Cost: {taskGlobalCost:C}{globalCostWarning}";
            yield return string.Empty;

            foreach (var taskGroup in taskGroups)
            {
                yield return "--------------------------------------------------";

                var taskName = taskGroup.Key;
                var taskCosts = taskGroup.Select(x => x.Cost).ToList();

                // --- Level 1: Task Totals ---
                var taskTotalTime = taskCosts.Aggregate(TimeSpan.Zero, (acc, c) => acc + c.TimeTaken);
                var taskTotalCost = CalculateTotalDollars(taskCosts);
                var totalItemsInRequest = taskCosts.Aggregate(0, (acc, c) => acc + c.NbItemsInRequest);
                var totalItemsInResponse = taskCosts.Aggregate(0, (acc, c) => acc + c.NbItemsInResponse);
                var taskNbCalls = taskCosts.Count;

                var costWarning = taskCosts.Any(c => IsFallbackCost(c)) ? NoteForResponseWithoutTokens : "";

                yield return $"Task: {taskName}";
                yield return $"   Totals:";
                yield return $"      Calls: {taskNbCalls}";
                yield return $"      Time Taken: {taskTotalTime:hh\\:mm\\:ss}";
                yield return $"      Cost: {taskTotalCost:C}{costWarning}";
                var retryString = (totalItemsInRequest > totalItemsInResponse) ? $" ({totalItemsInRequest - totalItemsInResponse} retries)" : string.Empty;
                yield return $"      Primary nodes in request:  {totalItemsInRequest,4}{retryString}";
                yield return $"      Primary nodes in response: {totalItemsInResponse,4}";

                // 3. Group by Engine AND Fallback Status (Level 2)
                // We group by both to ensure 'Gemini' (Tokens) and 'Gemini' (Fallback/Errors) remain distinct
                var engineGroups = taskCosts
                    .GroupBy(c => new { c.EngineIdentifier, IsFallback = IsFallbackCost(c) })
                    .OrderBy(g => g.Key.IsFallback)
                    .ThenBy(g => g.Key.EngineIdentifier);

                foreach (var engineGroup in engineGroups)
                {
                    var engineId = engineGroup.Key.EngineIdentifier;
                    bool isFallback = engineGroup.Key.IsFallback;
                    var groupCosts = engineGroup.ToList();

                    // Aggregate specific engine volume
                    var aggregated = Cost.Sum(taskName, groupCosts);

                    // Calculate Financials for this engine
                    double groupTotalInputDollars = groupCosts.Sum(c => (c.Input.TotalTokens / 1_000_000.0) * c.Input.EstimatedCostPerMillionTokens);
                    double groupTotalOutputDollars = groupCosts.Sum(c => (c.Output.TotalTokens / 1_000_000.0) * c.Output.EstimatedCostPerMillionTokens);
                    double groupTotalDollars = groupTotalInputDollars + groupTotalOutputDollars;

                    // Determine display flags
                    bool showCost = groupTotalDollars > 0;
                    string sectionTitle = isFallback ? $"Engine (RESPONSE WITHOUT TOKENS): {engineId}" : $"Engine: {engineId}";

                    // Calculate average rates (only needed if showing cost)
                    double avgInputRate = (showCost && aggregated.TotalPromptTokens > 0)
                        ? (groupTotalInputDollars / aggregated.TotalPromptTokens) * 1_000_000
                        : 0;
                    double avgOutputRate = (showCost && aggregated.TotalCompletionTokens > 0)
                        ? (groupTotalOutputDollars / aggregated.TotalCompletionTokens) * 1_000_000
                        : 0;

                    // --- Level 2 Header ---
                    yield return $"   {sectionTitle}";
                    yield return isFallback
                        ? $"      Calls: {groupCosts.Count}, Time: {aggregated.TimeTaken:hh\\:mm\\:ss}"
                        : $"      Calls: {groupCosts.Count}, Time: {aggregated.TimeTaken:hh\\:mm\\:ss}, Cost: {groupTotalDollars:C}";

                    // --- Level 3: Details (Input) ---
                    yield return isFallback
                        ? $"      Input:"
                        : $"      Input: {FormatMetric(aggregated.TotalPromptTokens, "tokens")}, {groupTotalInputDollars:C}";
                 
                    foreach (var sectionKvp in aggregated.Input.Sections.OrderBy(k => k.Key))
                    {
                        double sectionTotal = sectionKvp.Value.Total;
                        double sectionCost = (sectionTotal / 1_000_000.0) * avgInputRate;

                        // Clean up the Enum string for display
                        string sectionName = (sectionKvp.Key & ~AIRequestSection.FALLBACK).ToString();
                        yield return isFallback
                            ? $"         {sectionName}:"
                            : $"         {sectionName}: {FormatMetric(sectionTotal, "tokens")}, {sectionCost:C}";

                        foreach (var modality in sectionKvp.Value.Modality)
                        {
                            string key = modality.Key;
                            var count = modality.Value.Count;

                            var amount = modality.Value.Amount;
                            var unit = isFallback ? GetUnitForFallbackModality(key) : "tokens";

                            var modCost = (amount / 1_000_000.0) * avgInputRate;
                            var modCostStr = modCost > 0 ? $", {modCost:C}" : "";

                            yield return $"            {key} ({count}): {FormatMetric(amount, unit)}{modCostStr}";
                        }
                    }

                    // --- Level 3: Details (Output) ---
                    yield return isFallback
                        ? $"      Output:"
                        : $"      Output: {FormatMetric(aggregated.TotalCompletionTokens, "tokens")}, {groupTotalOutputDollars:C}";

                    foreach (var sectionKvp in aggregated.Output.Sections.OrderBy(k => k.Key))
                    {
                        double sectionTotal = sectionKvp.Value.Total;
                        double sectionCost = (sectionTotal / 1_000_000.0) * avgOutputRate;

                        string sectionName = (sectionKvp.Key & ~AIResponseSection.FALLBACK).ToString();
                        yield return isFallback 
                            ? $"         {sectionName}:"
                            : $"         {sectionName}: {FormatMetric(sectionTotal, "tokens")}, {sectionCost:C}";

                        foreach (var modality in sectionKvp.Value.Modality)
                        {
                            string key = modality.Key;
                            var count = modality.Value.Count;

                            var amount = modality.Value.Amount;
                            var unit = isFallback ? GetUnitForFallbackModality(key) : "tokens";

                            var modCost = (amount / 1_000_000.0) * avgOutputRate;
                            var modCostStr = modCost > 0 ? $", {modCost:C}" : "";

                            yield return $"            {key} ({count}): {FormatMetric(amount, unit)}{modCostStr}";
                        }
                    }

                    yield return string.Empty; // Empty line between engines
                }
            }
        }

        private string FormatMetric(double value, string unit)
        {
            // If it looks like an integer, print as integer
            if (Math.Abs(value % 1) <= (Double.Epsilon * 100))
            {
                return $"{(long)value} {unit}";
            }
            return $"{value:F2} {unit}";
        }

        private string GetUnitForFallbackModality(string modalityKey)
        {
            if (string.IsNullOrEmpty(modalityKey)) return "units";

            var key = modalityKey.ToUpperInvariant();
            if (key.Contains("TEXT")) return "chars";
            if (key.Contains("AUDIO")) return "seconds";
            if (key.Contains("IMG") || key.Contains("IMAGE")) return "images";

            // Default fallback based on standard types
            return "units";
        }
    }
}