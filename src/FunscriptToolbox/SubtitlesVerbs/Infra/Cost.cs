using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class Cost
    {
        public string TaskName { get; }
        public string EngineIdentifier { get; }
        public TimeSpan TimeTaken { get; }
        public int NbItemsInRequest { get; }
        public int NbItemsInResponse { get; set; }
        public InputCostDetails Input { get; }
        public OutputCostDetails Output { get; }
        public ExpandoObject CustomInfos { get; set; }
        public DateTime? CreationTime { get; }

        public Cost(string taskName, string engineIdentifier, TimeSpan timeTaken, int nbItemsInRequest, int? nbItemsInResponse = null, InputCostDetails input = null, OutputCostDetails output = null, ExpandoObject customInfos = null, DateTime? creationTime = null)
        {
            TaskName = taskName;
            EngineIdentifier = engineIdentifier;
            TimeTaken = timeTaken;
            NbItemsInRequest = nbItemsInRequest;
            NbItemsInResponse = nbItemsInResponse ?? nbItemsInRequest;
            Input = input ?? new InputCostDetails();
            Output = output ?? new OutputCostDetails();
            CustomInfos = customInfos;
            CreationTime = creationTime ?? DateTime.Now;
        }

        // --- Aggregation Logic ---

        public static Cost Sum(string newTaskName, IList<Cost> costs)
        {
            if (!costs.Any()) return null;

            var totalTimeTaken = costs.Aggregate(TimeSpan.Zero, (acc, c) => acc + c.TimeTaken);
            var totalItemsInRequest = costs.Sum(c => c.NbItemsInRequest);
            var totalItemsInResponse = costs.Sum(c => c.NbItemsInResponse);

            void AggregateSections<T>(
                Dictionary<T, ModalityBreakdown> target,
                IEnumerable<Dictionary<T, ModalityBreakdown>> sources)
            {
                foreach (var sourceDict in sources)
                {
                    foreach (var kvp in sourceDict)
                    {
                        if (!target.ContainsKey(kvp.Key))
                        {
                            target[kvp.Key] = new ModalityBreakdown();
                        }
                        target[kvp.Key].Add(kvp.Value);
                    }
                }
            }

            // 1. Aggregate Input
            var aggregatedInput = new InputCostDetails
            {
                EstimatedCostPerMillionTokens = costs.First().Input.EstimatedCostPerMillionTokens,
                Tokens = costs.Sum(c => c.Input.Tokens)
            };
            AggregateSections(aggregatedInput.Sections, costs.Select(c => c.Input.Sections));

            // 2. Aggregate Output
            var aggregatedOutput = new OutputCostDetails
            {
                EstimatedCostPerMillionTokens = costs.First().Output.EstimatedCostPerMillionTokens,
                ThoughtsTokens = costs.Sum(c => c.Output.ThoughtsTokens),
                CandidatesTokens = costs.Sum(c => c.Output.CandidatesTokens)
            };
            AggregateSections(aggregatedOutput.Sections, costs.Select(c => c.Output.Sections));

            // 3. TODO, maybe, one day, accumulate the field in customInfos that look like a number or a TimeSpan

            return new Cost(newTaskName, costs.First().EngineIdentifier, totalTimeTaken, totalItemsInRequest, totalItemsInResponse, aggregatedInput, aggregatedOutput);
        }

        // --- Creation Logic ---

        public static Cost Create(
            string taskName,
            string engineIdentifier,
            AIRequestPart[] allParts,
            TimeSpan timeTaken,
            int nbItemsInRequest,
            int nbItemsInResponse,
            double costPerInputMillionTokens,
            double costPerOutputMillionTokens,
            int? inputTokens,
            int outputThoughtsChars,
            int? outputThoughtsTokens,
            int outputCandidatesChars,
            int? outputCandidatesTokens,
            Dictionary<string, int> rawUsageInput = null,
            dynamic customInfos = null)
        {
            return (inputTokens != null && outputThoughtsTokens != null && outputCandidatesTokens != null)
                ? CreateTokenBasedCost(
                    taskName, engineIdentifier, allParts, timeTaken, nbItemsInRequest, nbItemsInResponse,
                    costPerInputMillionTokens, costPerOutputMillionTokens, inputTokens.Value,
                    outputThoughtsChars, outputThoughtsTokens.Value,
                    outputCandidatesChars, outputCandidatesTokens.Value, rawUsageInput, customInfos)
                : CreateFallbackCost(
                taskName, engineIdentifier, allParts, timeTaken, nbItemsInRequest, nbItemsInResponse,
                outputThoughtsChars + outputCandidatesChars, customInfos);
        }

        private static Cost CreateTokenBasedCost(
            string taskName,
            string engineIdentifier,
            AIRequestPart[] allParts,
            TimeSpan timeTaken,
            int nbItemsInRequest,
            int nbItemsInResponse,
            double costPerInputMillionTokens,
            double costPerOutputMillionTokens,
            int totalInputTokens,
            int outputThoughtsChars,
            int outputThoughtsTokens,
            int outputCandidatesChars,
            int outputCandidatesTokens,
            Dictionary<string, int> rawUsageInput,
            dynamic customInfos)
        {
            // Calculate Total Estimated Weight to distribute Actual Tokens
            double totalEstimatedWeight = allParts.Sum(p => p.EstimatedTokens);
            if (totalEstimatedWeight <= 0) totalEstimatedWeight = 1; // Prevent div by zero

            var inputDetails = new InputCostDetails
            {
                EstimatedCostPerMillionTokens = costPerInputMillionTokens,
                Tokens = totalInputTokens
            };

            foreach (var sectionGroup in allParts.GroupBy(p => p.Section))
            {
                var breakdown = new ModalityBreakdown();

                // Group parts by Modality (Text, Image, Audio) within this section
                foreach (var modalityGroup in sectionGroup.GroupBy(p => p.Modality))
                {
                    string modality = modalityGroup.Key;
                    var parts = modalityGroup.ToList();

                    // 1. Calculate Physical Units (Chars, Seconds, Images)
                    double units = parts.Sum(p => p.Units);
                    string unitName = parts.FirstOrDefault()?.UnitName ?? "units";
                    double estimatedTokens = parts.Sum(p => p.EstimatedTokens);
                    int count = parts.Count;

                    // 2. Calculate Actual Tokens (Distributed)
                    // If the provider gives specific breakdown (rawUsageInput), use it. 
                    // Otherwise distribute totalInputTokens based on estimated weight.
                    double actualTokensForModality = 0;

                    if (rawUsageInput != null && rawUsageInput.TryGetValue(modality, out int specificUsage))
                    {
                        // The provider gave us exactly how many tokens this modality used
                        // We assume the provider's breakdown applies to the whole request, 
                        // so we weight it by this section's share of that modality.
                        var totalEstForModality = allParts.Where(p => p.Modality == modality).Sum(p => p.EstimatedTokens);
                        if (totalEstForModality > 0)
                        {
                            actualTokensForModality = (estimatedTokens / totalEstForModality) * specificUsage;
                        }
                    }
                    else
                    {
                        // Proportional distribution of the global total
                        actualTokensForModality = (estimatedTokens / totalEstimatedWeight) * totalInputTokens;
                    }

                    breakdown.Add(modality, count, units, unitName, estimatedTokens, actualTokensForModality);
                }
                inputDetails.Sections[sectionGroup.Key] = breakdown;
            }

            // Handle Output
            var outputDetails = new OutputCostDetails 
            {
                EstimatedCostPerMillionTokens = costPerOutputMillionTokens,
                ThoughtsTokens = outputThoughtsTokens,
                CandidatesTokens = outputCandidatesTokens
            };
            if (outputThoughtsTokens > 0)
            {
                var thoughtsBreakdown = new ModalityBreakdown();
                thoughtsBreakdown.Add("TEXT", 1, outputThoughtsChars, "chars", AIRequestPartText.GetEstimatedTokensFromChar(outputThoughtsChars), outputThoughtsTokens);
                outputDetails.Sections[AIResponseSection.Thoughts] = thoughtsBreakdown;
            }

            if (outputCandidatesTokens > 0)
            {
                var candidatesBreakdown = new ModalityBreakdown();
                candidatesBreakdown.Add("TEXT", 1, outputCandidatesChars, "chars", AIRequestPartText.GetEstimatedTokensFromChar(outputCandidatesChars), outputCandidatesTokens);
                outputDetails.Sections[AIResponseSection.Candidates] = candidatesBreakdown;
            }

            return new Cost(taskName, engineIdentifier, timeTaken, nbItemsInRequest, nbItemsInResponse, inputDetails, outputDetails, customInfos);
        }

        private static Cost CreateFallbackCost(
            string taskName,
            string engineIdentifier,
            AIRequestPart[] allParts,
            TimeSpan timeTaken,
            int nbItemsInRequest,
            int nbItemsInResponse,
            int nbCharsInResponse,
            dynamic customInfos)
        {
            var inputDetails = new InputCostDetails { EstimatedCostPerMillionTokens = 0 };

            foreach (var sectionGroup in allParts.GroupBy(p => p.Section))
            {
                var breakdown = new ModalityBreakdown();

                foreach (var modalityGroup in sectionGroup.GroupBy(p => p.Modality))
                {
                    string modality = modalityGroup.Key;
                    var parts = modalityGroup.ToList();

                    double units = parts.Sum(p => p.Units);
                    string unitName = parts.FirstOrDefault()?.UnitName ?? "units";
                    double estimatedTokens = parts.Sum(p => p.EstimatedTokens);
                    int count = parts.Count;

                    // Actual tokens are 0 in fallback
                    breakdown.Add(modality, count, units, unitName, estimatedTokens, 0);
                }
                inputDetails.Sections[sectionGroup.Key | AIRequestSection.FALLBACK] = breakdown;
            }

            var outputDetails = new OutputCostDetails 
            { 
                EstimatedCostPerMillionTokens = 0 
            };

            if (nbCharsInResponse > 0)
            {
                var candidatesBreakdown = new ModalityBreakdown();
                double estimatedOutputTokens = AIRequestPartText.GetEstimatedTokensFromChar(nbCharsInResponse);
                candidatesBreakdown.Add("TEXT", 1, nbCharsInResponse, "chars", estimatedOutputTokens, 0);
                outputDetails.Sections[AIResponseSection.Candidates | AIResponseSection.FALLBACK] = candidatesBreakdown;
            }

            return new Cost(taskName, engineIdentifier, timeTaken, nbItemsInRequest, nbItemsInResponse, inputDetails, outputDetails, customInfos);
        }

        public class InputCostDetails
        {
            public double EstimatedCostPerMillionTokens { get; set; }
            public int Tokens { get; set; }
            public double EstimatedCost => (double)Tokens / 1_000_000 * EstimatedCostPerMillionTokens;
            public Dictionary<AIRequestSection, ModalityBreakdown> Sections { get; } = new Dictionary<AIRequestSection, ModalityBreakdown>();
        }

        public class OutputCostDetails
        {
            public double EstimatedCostPerMillionTokens { get; set; }
            public int ThoughtsTokens { get; set; }
            public int CandidatesTokens { get; set; }
            public int Tokens => ThoughtsTokens + CandidatesTokens;
            public double EstimatedCost => (double)Tokens / 1_000_000 * EstimatedCostPerMillionTokens;
            public Dictionary<AIResponseSection, ModalityBreakdown> Sections { get; } = new Dictionary<AIResponseSection, ModalityBreakdown>();
        }

        public class ModalityBreakdown
        {
            public Dictionary<string, MetricStat> Modality { get; } = new Dictionary<string, MetricStat>(StringComparer.OrdinalIgnoreCase);

            [JsonIgnore]
            public double TotalActualTokens => Modality.Values.Sum(m => m.ActualTokens);

            public void Add(string modality, int count, double units, string unitName, double estimatedTokens, double actualTokens)
            {
                if (!Modality.ContainsKey(modality))
                {
                    Modality[modality] = new MetricStat { UnitName = unitName };
                }

                var stat = Modality[modality];
                stat.Count += count;
                stat.Units += units;
                stat.EstimatedTokens += estimatedTokens;
                stat.ActualTokens += actualTokens;

                // Keep UnitName consistent, or comma separate if they differ (rare)
                if (stat.UnitName != unitName && !stat.UnitName.Contains(unitName))
                    stat.UnitName = $"{stat.UnitName}/{unitName}";
            }

            public void Add(ModalityBreakdown other)
            {
                if (other == null) return;
                foreach (var kvp in other.Modality)
                {
                    Add(kvp.Key, kvp.Value.Count, kvp.Value.Units, kvp.Value.UnitName, kvp.Value.EstimatedTokens, kvp.Value.ActualTokens);
                }
            }
        }

        public class MetricStat
        {
            public int Count { get; set; }

            /// <summary>
            /// Physical units (Chars, Seconds, Images count)
            /// </summary>
            public double Units { get; set; }
            public string UnitName { get; set; }

            /// <summary>
            /// Local calculation of tokens
            /// </summary>
            public double EstimatedTokens { get; set; }

            /// <summary>
            /// The billed tokens (distributed or exact)
            /// </summary>
            public double ActualTokens { get; set; }
        }
    }
}