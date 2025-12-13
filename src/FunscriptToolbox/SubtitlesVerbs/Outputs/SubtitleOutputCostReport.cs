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
                double input = costsToSum.Sum(c => c.Input.EstimatedCost);
                double output = costsToSum.Sum(c => c.Output.EstimatedCost);
                return input + output;
            }

            // Helper: Check for fallback
            bool IsFallbackCost(Cost c)
            {
                return c.Input.Sections.Keys.Any(k => k.HasFlag(AIRequestSection.FALLBACK))
                    || c.Output.Sections.Keys.Any(k => k.HasFlag(AIResponseSection.FALLBACK));
            }

            // Helper: Format numbers nicely
            string Fmt(double val) => $"{val:#,##0.##}";

            // 2. Report Header
            var taskGroups = allCosts
                .GroupBy(x => x.GroupName)
                .OrderBy(g => g.First().Cost.CreationTime);

            const string NoteForFallback = " [NOTE: Responses from 'Engine (NO TOKENS REPORTED)' are not counted, see below]";
            var taskGlobalCost = CalculateTotalDollars(allCosts.Select(f => f.Cost));
            var globalCostWarning = allCosts.Any(c => IsFallbackCost(c.Cost)) ? NoteForFallback : "";

            yield return "========================================================================";
            yield return " AI COST REPORT";
            yield return " All costs are estimates based on token usage reported by the API.";
            yield return " Please refer to your vendor billing for the actual final charges.";
            yield return "========================================================================";
            yield return string.Empty;
            yield return "Global:";
            yield return $"   Calls: {taskGroups.SelectMany(f => f).Count()}";
            yield return $"   Time : {allCosts.Aggregate(TimeSpan.Zero, (acc, c) => acc + c.Cost.TimeTaken):hh\\:mm\\:ss}";
            yield return $"   Input Tokens: {Fmt(allCosts.Sum(c => c.Cost.Input.Tokens))}";
            yield return $"   Output Tokens: {Fmt(allCosts.Sum(c => c.Cost.Output.Tokens))}";
            yield return $"   Cost: {taskGlobalCost:C}{globalCostWarning}";
            yield return string.Empty;

            foreach (var taskGroup in taskGroups)
            {
                yield return "--------------------------------------------------";

                var taskName = taskGroup.Key;
                var taskCosts = taskGroup.Select(x => x.Cost).ToList();

                // Level 1: Task Totals
                yield return $"Task: {taskName}";
                yield return $"   Totals:";
                yield return $"      Calls: {taskCosts.Count}";
                yield return $"      Time : {taskCosts.Aggregate(TimeSpan.Zero, (acc, c) => acc + c.TimeTaken):hh\\:mm\\:ss}";
                yield return $"      Input Tokens: {Fmt(taskCosts.Sum(c => c.Input.Tokens))}";
                yield return $"      Output Tokens: {Fmt(taskCosts.Sum(c => c.Output.Tokens))}";
                yield return $"      Cost : {CalculateTotalDollars(taskCosts):C}";

                var totalItemsInRequest = taskCosts.Sum(c => c.NbItemsInRequest);
                var totalItemsInResponse = taskCosts.Sum(c => c.NbItemsInResponse);
                var retryString = (totalItemsInRequest > totalItemsInResponse) ? $" ({totalItemsInRequest - totalItemsInResponse} retries)" : string.Empty;
                yield return $"      Primary nodes in request:  {totalItemsInRequest,4}{retryString}";
                yield return $"      Primary nodes in response: {totalItemsInResponse,4}";

                // Level 2: Engine Groups (Separating Fallback/No-Token responses)
                var engineGroups = taskCosts
                    .GroupBy(c => new { c.EngineIdentifier, IsFallback = IsFallbackCost(c) })
                    .OrderBy(g => g.Key.IsFallback) // Standard first, Fallback last
                    .ThenBy(g => g.Key.EngineIdentifier);

                foreach (var engineGroup in engineGroups)
                {
                    var engineId = engineGroup.Key.EngineIdentifier;
                    bool isFallback = engineGroup.Key.IsFallback;
                    var groupCosts = engineGroup.ToList();

                    // Aggregate
                    var aggregated = Cost.Sum(taskName, groupCosts);

                    double groupInputCost = aggregated.Input.EstimatedCost;
                    double groupOutputCost = aggregated.Output.EstimatedCost;
                    double groupTotalCost = groupInputCost + groupOutputCost;

                    // Calculate Rates for display
                    double avgInputRate = aggregated.Input.EstimatedCostPerMillionTokens;
                    double avgOutputRate = aggregated.Output.EstimatedCostPerMillionTokens;

                    string title = isFallback ? $"Engine (NO TOKENS REPORTED): {engineId}" : $"Engine: {engineId}";

                    yield return $"   {title}";
                    yield return $"      Calls: {groupCosts.Count}, Time: {aggregated.TimeTaken:hh\\:mm\\:ss}{(isFallback ? "" : $", Cost: {groupTotalCost:C}")}";

                    // --- INPUT ---
                    yield return isFallback
                        ? $"      Input (Estimates only):"
                        : $"      Input: {Fmt(aggregated.Input.Tokens)} tokens, {groupInputCost:C}";

                    foreach (var sectionKvp in aggregated.Input.Sections.OrderBy(k => k.Key))
                    {
                        string sectionName = (sectionKvp.Key & ~AIRequestSection.FALLBACK).ToString();
                        double sectionTotalTokens = sectionKvp.Value.TotalActualTokens;
                        double sectionCost = (sectionTotalTokens / 1_000_000.0) * avgInputRate;

                        if (!isFallback)
                        {
                            yield return $"         {sectionName}: {Fmt(sectionTotalTokens)} tokens, {sectionCost:C}";
                        }
                        else
                        {
                            yield return $"         {sectionName}:";
                        }

                        foreach (var modality in sectionKvp.Value.Modality)
                        {
                            string key = modality.Key;
                            var stat = modality.Value;

                            double modCost = (stat.ActualTokens / 1_000_000.0) * avgInputRate;
                            string costStr = (!isFallback && modCost > 0) ? $", {modCost:C}" : "";

                            // Logic to display tokens: if fallback, show estimated tokens with a label
                            string tokenStr = isFallback
                                ? $"{Fmt(stat.EstimatedTokens)} est. tokens"
                                : $"{Fmt(stat.ActualTokens)} tokens";

                            yield return $"            {key} ({stat.Count}): {Fmt(stat.Units)} {stat.UnitName}, {tokenStr}{costStr}";
                        }
                    }

                    // --- OUTPUT ---
                    yield return isFallback
                        ? $"      Output (Estimates only):"
                        : $"      Output: {Fmt(aggregated.Output.Tokens)} tokens, {groupOutputCost:C}";

                    foreach (var sectionKvp in aggregated.Output.Sections.OrderBy(k => k.Key))
                    {
                        string sectionName = (sectionKvp.Key & ~AIResponseSection.FALLBACK).ToString();
                        double sectionTotalTokens = sectionKvp.Value.TotalActualTokens;
                        double sectionCost = (sectionTotalTokens / 1_000_000.0) * avgOutputRate;

                        if (!isFallback)
                        {
                            yield return $"         {sectionName}: {Fmt(sectionTotalTokens)} tokens, {sectionCost:C}";
                        }
                        else
                        {
                            yield return $"         {sectionName}:";
                        }

                        foreach (var modality in sectionKvp.Value.Modality)
                        {
                            string key = modality.Key;
                            var stat = modality.Value;

                            double modCost = (stat.ActualTokens / 1_000_000.0) * avgOutputRate;
                            string costStr = (!isFallback && modCost > 0) ? $", {modCost:C}" : "";

                            string tokenStr = isFallback
                               ? $"{Fmt(stat.EstimatedTokens)} est. tokens"
                               : $"{Fmt(stat.ActualTokens)} tokens";

                            yield return $"            {key} ({stat.Count}): {Fmt(stat.Units)} {stat.UnitName}, {tokenStr}{costStr}";
                        }
                    }
                    yield return string.Empty;
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