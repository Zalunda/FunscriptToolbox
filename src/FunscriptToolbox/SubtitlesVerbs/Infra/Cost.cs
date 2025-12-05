using Newtonsoft.Json;
using System;
using System.Collections.Generic;
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
        public DateTime? CreationTime { get; }


        // High level totals
        [JsonIgnore]
        public int TotalPromptTokens => (int)Input.TotalTokens;

        [JsonIgnore]
        public int TotalCompletionTokens => (int)Output.TotalTokens;

        [JsonIgnore]
        public int TotalTokens => TotalPromptTokens + TotalCompletionTokens;

        public Cost(string taskName, string engineIdentifier, TimeSpan timeTaken, int nbItemsInRequest, int? nbItemsInResponse = null, InputCostDetails input = null, OutputCostDetails output = null, DateTime? creationTime = null)
        {
            TaskName = taskName;
            EngineIdentifier = engineIdentifier;
            TimeTaken = timeTaken;
            NbItemsInRequest = nbItemsInRequest;
            NbItemsInResponse = nbItemsInResponse ?? nbItemsInRequest;
            Input = input ?? new InputCostDetails();
            Output = output ?? new OutputCostDetails();
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
            var aggregatedInput = new InputCostDetails();
            AggregateSections(aggregatedInput.Sections, costs.Select(c => c.Input.Sections));

            // 2. Aggregate Output
            var aggregatedOutput = new OutputCostDetails();
            AggregateSections(aggregatedOutput.Sections, costs.Select(c => c.Output.Sections));

            return new Cost(newTaskName, costs.First().EngineIdentifier, totalTimeTaken, totalItemsInRequest, totalItemsInResponse, aggregatedInput, aggregatedOutput);
        }

        // --- Creation/Distribution Logic ---

        public static Cost Create(
            string taskName,
            string engineIdentifier,
            AIRequest request,
            string assistantMessage,
            TimeSpan timeTaken,
            int nbItemsInRequest,
            int nbItemsInResponse,
            double costPerInputMillionTokens,
            double costPerOutputMillionTokens,
            int? inputTokens,
            int? outputThoughtsTokens,
            int? outputCandidatesTokens,
            Dictionary<string, int> rawUsageInput = null)
        {
            return (inputTokens != null && outputThoughtsTokens != null && outputCandidatesTokens != null)
                ? CreateTokenBasedCost(
                    taskName, engineIdentifier, request, timeTaken, nbItemsInRequest, nbItemsInResponse,
                    costPerInputMillionTokens, costPerOutputMillionTokens,
                    inputTokens.Value, outputThoughtsTokens.Value, outputCandidatesTokens.Value, rawUsageInput)
                : CreateFallbackCost(
                    taskName, engineIdentifier, request, timeTaken, nbItemsInRequest, nbItemsInResponse, 
                    assistantMessage.Length);
        }

        private static Cost CreateTokenBasedCost(
            string taskName,
            string engineIdentifier,
            AIRequest originalRequest,
            TimeSpan timeTaken,
            int nbItemsInRequest,
            int nbItemsInResponse,
            double costPerInputMillionTokens,
            double costPerOutputMillionTokens,
            int inputTokens,
            int outputThoughtsTokens,
            int outputCandidatesTokens,
            Dictionary<string, int> rawUsageInput)
        {
            var allParts = originalRequest.SystemParts.Concat(originalRequest.UserParts).ToList();
            var totalWeightsByModality = allParts
                .GroupBy(p => p.Modality)
                .ToDictionary(g => g.Key, g => g.Sum(p => p.Weight));

            var inputDetails = new InputCostDetails { EstimatedCostPerMillionTokens = costPerInputMillionTokens };

            double Distribute(string modality, double partWeight)
            {
                int totalTokens = 0;
                if (rawUsageInput != null && rawUsageInput.TryGetValue(modality, out int t))
                    totalTokens = t;
                else if (string.Equals(modality, "Text", StringComparison.InvariantCultureIgnoreCase) && rawUsageInput == null)
                    totalTokens = inputTokens;

                if (!totalWeightsByModality.TryGetValue(modality, out double totalWeight) || totalWeight <= 0) return 0;
                return (int)Math.Round((partWeight / totalWeight) * totalTokens);
            }

            foreach (var sectionGroup in allParts.GroupBy(p => p.Section))
            {
                var breakdown = new ModalityBreakdown();
                var modalitiesInSection = sectionGroup.Select(p => p.Modality).Distinct();
                if (!modalitiesInSection.Any())
                {
                    modalitiesInSection = new[] { "UNKNOWN" };
                }

                foreach (var modality in modalitiesInSection)
                {
                    // Filter the specific parts for this section and modality
                    var parts = sectionGroup.Where(p => p.Modality == modality).ToList();
                    var sectionModalityWeight = parts.Sum(p => p.Weight);

                    int count = parts.Count;

                    double distributedTokens = Distribute(modality, sectionModalityWeight);
                    if (distributedTokens > 0)
                    {
                        breakdown.Add(modality, distributedTokens, count);
                    }
                }
                inputDetails.Sections[sectionGroup.Key] = breakdown;
            }

            var outputDetails = new OutputCostDetails { EstimatedCostPerMillionTokens = costPerOutputMillionTokens };

            // 1. Thoughts (Reasoning)
            if (outputThoughtsTokens > 0)
            {
                var thoughtsBreakdown = new ModalityBreakdown();
                // Thoughts are usually 1 block of text
                thoughtsBreakdown.Add("Text", outputThoughtsTokens, 1);
                outputDetails.Sections[AIResponseSection.Thoughts] = thoughtsBreakdown;
            }

            // 2. Candidates (The actual response)
            if (outputCandidatesTokens > 0)
            {
                var candidatesBreakdown = new ModalityBreakdown();
                candidatesBreakdown.Add("Text", outputCandidatesTokens, 1);
                outputDetails.Sections[AIResponseSection.Candidates] = candidatesBreakdown;
            }

            return new Cost(taskName, engineIdentifier, timeTaken, nbItemsInRequest, nbItemsInResponse, inputDetails, outputDetails);
        }

        private static Cost CreateFallbackCost(
            string taskName,
            string engineIdentifier,
            AIRequest originalRequest,
            TimeSpan timeTaken,
            int nbItemsInRequest,
            int nbItemsInResponse,
            int nbCharsInResponse)
        {
            var allParts = originalRequest.SystemParts.Concat(originalRequest.UserParts).ToList();
            var inputDetails = new InputCostDetails
            {
                EstimatedCostPerMillionTokens = 0
            };

            foreach (var sectionGroup in allParts.GroupBy(p => p.Section))
            {
                var sectionName = sectionGroup.Key;
                var breakdown = new ModalityBreakdown();

                foreach (var modalityGroup in sectionGroup.GroupBy(p => p.Modality))
                {
                    var modality = modalityGroup.Key;
                    double measuredAmount = 0;
                    int count = modalityGroup.Count();

                    if (string.Equals(modality, "Text", StringComparison.OrdinalIgnoreCase))
                    {
                        measuredAmount = modalityGroup.OfType<AIRequestPartText>().Sum(p => p.Content?.Length ?? 0);
                    }
                    else if (string.Equals(modality, "Image", StringComparison.OrdinalIgnoreCase))
                    {
                        measuredAmount = count; // For images, amount and count are often the same in fallback
                    }
                    else
                    {
                        measuredAmount = modalityGroup.OfType<AIRequestPartAudio>().Sum(p => p.Duration.TotalSeconds);
                    }

                    if (measuredAmount > 0)
                    {
                        breakdown.Add(modality, measuredAmount, count);
                    }
                }
                inputDetails.Sections[sectionName | AIRequestSection.FALLBACK] = breakdown;
            }

            var outputDetails = new OutputCostDetails { EstimatedCostPerMillionTokens = 0 };

            if (nbCharsInResponse > 0)
            {
                var candidatesBreakdown = new ModalityBreakdown();
                candidatesBreakdown.Add("TEXT", nbCharsInResponse, 1);
                outputDetails.Sections[AIResponseSection.Candidates | AIResponseSection.FALLBACK] = candidatesBreakdown;
            }

            return new Cost(taskName, engineIdentifier, timeTaken, nbItemsInRequest, nbItemsInResponse, inputDetails, outputDetails);
        }

        public class InputCostDetails
        {
            public double EstimatedCostPerMillionTokens { get; set; }

            public Dictionary<AIRequestSection, ModalityBreakdown> Sections { get; }
                = new Dictionary<AIRequestSection, ModalityBreakdown>();

            [JsonIgnore]
            public double TotalTokens => Sections.Where(kvp => !kvp.Key.HasFlag(AIRequestSection.FALLBACK)).Sum(s => s.Value.Total);
        }

        public class OutputCostDetails
        {
            public double EstimatedCostPerMillionTokens { get; set; }

            public Dictionary<AIResponseSection, ModalityBreakdown> Sections { get; }
                = new Dictionary<AIResponseSection, ModalityBreakdown>();

            [JsonIgnore]
            public double TotalTokens => Sections.Where(kvp => !kvp.Key.HasFlag(AIResponseSection.FALLBACK)).Sum(s => s.Value.Total);
        }

        public class ModalityBreakdown
        {
            public Dictionary<string, MetricStat> Modality { get; }
                = new Dictionary<string, MetricStat>(StringComparer.OrdinalIgnoreCase);

            [JsonIgnore]
            public double Total => Modality.Values.Sum(m => m.Amount);

            public void Add(string modality, double amount, int count)
            {
                if (!Modality.ContainsKey(modality))
                {
                    Modality[modality] = new MetricStat();
                }
                Modality[modality].Amount += amount;
                Modality[modality].Count += count;
            }

            public void Add(ModalityBreakdown other)
            {
                if (other == null) return;
                foreach (var kvp in other.Modality)
                {
                    Add(kvp.Key, kvp.Value.Amount, kvp.Value.Count);
                }
            }
        }

        public class MetricStat
        {
            public double Amount { get; set; }
            public int Count { get; set; }
        }
    }
}