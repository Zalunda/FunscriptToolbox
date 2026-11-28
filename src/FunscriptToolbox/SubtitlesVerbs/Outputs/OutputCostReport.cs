using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using FunscriptToolbox.SubtitlesVerbs.Processors;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbs.Outputs
{
    [ConfigDoc("Writes the cost of the AI requests of the scene (FileSuffix, ex. '.cost.txt'): by step, the tokens and the estimated price (from the EstimatedCost... of the engines). OutputToConsole: also in the log.",
        Advice = "Keep CanBeUpdated: written again at each run, with the costs of the steps done since.")]
    internal class OutputCostReport : SubtitleOutput
    {
        public OutputCostReport()
        {
        }

        [ConfigDoc("The suffix of the file written, next to the scene (ex. \".cost.txt\").")]
        [JsonProperty(Order = 5, Required = Required.Always)]
        public string FileSuffix { get; set; }
        [ConfigDoc("Also written in the log of the scene.")]
        [JsonProperty(Order = 10)]
        public bool OutputToConsole { get; set; } = true;

        // (a report on all the tracks: no arrow from each of them, only its file)
        protected override void DescribeDataFlow(WorkerDataFlow flow)
        {
            base.DescribeDataFlow(flow);
            flow.Output(DataKind.File, this.FileSuffix);
        }

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
                foreach (var line in GenerateConsolidatedReport(context.WIP.Tracks ?? new List<Track>()))
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

        // (the rules of the estimate: in Cost, shared with the web UI)
        private static bool IsFallback(Cost c) => Cost.IsFallback(c);
        private static bool IsAI(Cost c) => Cost.IsAI(c);
        private static double Dollars(IEnumerable<Cost> list) => Cost.Dollars(list);

        /// <summary>
        /// The total cost of the AI of a scene, in $ (the same as the 'Total' of the report: an estimate), or null when there is no AI cost yet.
        /// (read while the scene can be adding costs, on its own thread: a copy, made while no save is in progress; when a list changes
        /// during the copy, it throws: the caller shows nothing this time)
        /// </summary>
        public static double? GetTotalAICost(WorkInProgressSubtitles wip)
        {
            if (wip == null)
                return null;
            List<Cost> costs = null;
            wip.ExecuteLocked(() => costs = (wip.Tracks?.ToArray().SelectMany(t => t.Costs.ToArray()) ?? Enumerable.Empty<Cost>())
                .Where(c => c != null && IsAI(c))
                .ToList());
            return costs.Count == 0 ? (double?)null : Dollars(costs);
        }

        /// <summary>
        /// The AI cost of each worker of a scene, from the tracks it produces (the costs of a worker are kept in its own track): the
        /// estimate in $, the number of AI calls, and how many of them have no token count (not included in the $). Only the workers
        /// that have AI calls. (a copy, made like GetTotalAICost)
        /// </summary>
        public static Dictionary<SubtitleWorker, (double Dollars, int Calls, int Fallbacks)> GetAICostByWorker(
            WorkInProgressSubtitles wip,
            IEnumerable<(SubtitleWorker Worker, IReadOnlyCollection<string> TrackIds)> workers)
        {
            var result = new Dictionary<SubtitleWorker, (double, int, int)>();
            if (wip == null)
                return result;
            var list = workers.ToList();
            var costsByWorker = new List<(SubtitleWorker Worker, Cost[] Costs)>();
            wip.ExecuteLocked(() =>
            {
                var tracks = wip.Tracks?.ToArray() ?? Array.Empty<Track>();
                foreach (var (worker, trackIds) in list)
                {
                    var costs = tracks.Where(t => trackIds.Contains(t.Id)).SelectMany(t => t.Costs.ToArray())
                        .Where(c => c != null && IsAI(c))
                        .ToArray();
                    if (costs.Length > 0)
                        costsByWorker.Add((worker, costs));
                }
            });
            foreach (var (worker, costs) in costsByWorker)
                result[worker] = (Dollars(costs), costs.Length, costs.Count(IsFallback));
            return result;
        }

        /// <summary>
        /// A short summary in the log of the task (after a run, and when it's already done): the total, then one line per task
        /// (the details are in the report file).
        /// </summary>
        protected override IEnumerable<string> GetAdditionalStatusLines(SubtitleGeneratorContext context)
        {
            var costs = (context.WIP.Tracks?.SelectMany(t => t.Costs) ?? Enumerable.Empty<Cost>())
                .ToList();
            if (costs.Count == 0)
            {
                yield return "No cost yet.";
                yield break;
            }

            static string Time(TimeSpan t) => $"{(int)t.TotalHours}:{t:mm\\:ss}";
            static string Tokens(double n) => n >= 1_000_000 ? $"{n / 1_000_000:0.##}M" : n >= 1_000 ? $"{n / 1_000:0.#}k" : $"{n:0}";

            var aiCosts = costs.Where(IsAI).ToList();
            var fallbackNote = costs.Any(IsFallback) ? " (+ responses without token count, not included)" : string.Empty;
            yield return $"Total: {Dollars(aiCosts):C} for {aiCosts.Count} AI call(s){fallbackNote}, "
                + $"{Tokens(aiCosts.Sum(c => c.Input.Tokens))} tokens in / {Tokens(aiCosts.Sum(c => c.Output.Tokens))} out, "
                + $"{Time(aiCosts.Aggregate(TimeSpan.Zero, (acc, c) => acc + c.TimeTaken))} of AI time (estimate: see the bill of the vendor).";

            foreach (var task in costs.GroupBy(c => c.TaskName).OrderBy(g => g.Min(c => c.CreationTime)))
            {
                var ai = task.Where(IsAI).ToList();
                var other = task.Where(c => !IsAI(c)).ToList();
                var parts = new List<string>();
                if (ai.Count > 0)
                    parts.Add($"{Dollars(ai):C} ({ai.Count} call(s), {Time(ai.Aggregate(TimeSpan.Zero, (acc, c) => acc + c.TimeTaken))})");
                if (other.Count > 0)
                    parts.Add($"{Time(other.Aggregate(TimeSpan.Zero, (acc, c) => acc + c.TimeTaken))} ({string.Join(", ", other.Select(c => c.EngineIdentifier).Distinct())})");
                yield return $"   {task.Key}: {string.Join(", ", parts)}";
            }
            if (this.FileSuffix != null)
                yield return $"Details: '{Path.GetFileName(context.WIP.BaseFilePath + this.FileSuffix)}'.";
        }

        private IEnumerable<string> GenerateConsolidatedReport(IEnumerable<Track> tracks)
        {
            // 1. Flatten all costs into a manipulatable list, grouped by step (the name of their task)
            var allCosts = tracks
                .SelectMany(t => t.Costs)
                .Select(cost => (GroupName: cost.TaskName, Cost: cost))
                .ToList();

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