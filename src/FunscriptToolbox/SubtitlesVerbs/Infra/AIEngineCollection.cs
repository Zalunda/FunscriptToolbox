using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public sealed class AIEngineCollection : AIEngine
    {
        private static readonly ConcurrentDictionary<string, SkippedEngineInfo> s_skippedEngines =
            new ConcurrentDictionary<string, SkippedEngineInfo>();

        [JsonProperty(Order = 10, Required = Required.Always)]
        public List<AIEngineAPI> Engines { get; set; } = new List<AIEngineAPI>();

        [JsonProperty(Order = 11)]
        public bool SkipOnQuotasUsed { get; set; } = true;

        [JsonProperty(Order = 12)]
        public bool SkipOnServiceUnavailable { get; set; } = true;

        [JsonProperty(Order = 13)]
        public TimeSpan ServiceUnavailableRetryDelay { get; set; } = TimeSpan.FromMinutes(2);

        [JsonProperty(Order = 14)]
        public TimeSpan QuotasUsedRetryDelay { get; set; } = TimeSpan.FromHours(1);

        public override AIResponse Execute(
            SubtitleGeneratorContext context,
            AIRequest request)
        {
            if (Engines == null || Engines.Count == 0)
            {
                throw new InvalidOperationException("AIEngineCollection has no engines configured.");
            }

            var exceptions = new List<Exception>();
            var now = DateTime.UtcNow;

            foreach (var engine in Engines)
            {
                var engineId = engine.EngineIdentifier;

                // Check if this engine should be skipped
                if (s_skippedEngines.TryGetValue(engineId, out var skipInfo))
                {
                    if (now < skipInfo.SkipUntil)
                    {
                        var remainingTime = skipInfo.SkipUntil - now;
                        context.WriteInfo($"Skipping engine '{engineId}' due to {skipInfo.ErrorType}. " +
                            $"Will retry in {remainingTime.TotalMinutes:F1} minutes.");
                        continue;
                    }
                    else
                    {
                        // Retry delay has passed, remove from skipped list
                        s_skippedEngines.TryRemove(engineId, out _);
                        context.WriteInfo($"Retry delay passed for engine '{engineId}'. Attempting to use it again.");
                    }
                }

                if (!engine.Enabled)
                {
                    context.WriteInfo($"Skipping engine '{engineId}' because it's disabled.");
                    continue;
                }
                if (!engine.IsAPIKeyAvailableIfNeeded(context))
                {
                    context.WriteInfo($"Skipping engine '{engineId}' because it's disbled (API key not set).");
                    continue;
                }

                try
                {
                    context.WriteInfo($"Attempting to use engine: {engine.BaseAddress}, Model: {engine.Model}");
                    var response = engine.Execute(context, request);

                    // Success - make sure engine is not in skipped list
                    s_skippedEngines.TryRemove(engineId, out _);

                    return response;
                }
                catch (AIEngineAPIException ex)
                {
                    exceptions.Add(ex);
                    context.WriteInfo($"Engine '{engineId}' failed with {ex.ErrorType}: {ex.Message}");

                    bool shouldSkip = false;
                    TimeSpan retryDelay = TimeSpan.Zero;

                    switch (ex.ErrorType)
                    {
                        case AIEngineErrorType.QuotasUsed:
                            if (SkipOnQuotasUsed)
                            {
                                shouldSkip = true;
                                retryDelay = QuotasUsedRetryDelay;
                            }
                            break;
                        case AIEngineErrorType.ServiceUnavailable:
                            if (SkipOnServiceUnavailable)
                            {
                                shouldSkip = true;
                                retryDelay = ServiceUnavailableRetryDelay;
                            }
                            break;
                        default:
                            throw;
                    }

                    if (shouldSkip && retryDelay > TimeSpan.Zero)
                    {
                        var skipUntil = DateTime.UtcNow + retryDelay;
                        s_skippedEngines[engineId] = new SkippedEngineInfo(ex.ErrorType, skipUntil, ex.Message);
                        context.WriteInfo($"Engine '{engineId}' will be skipped until {skipUntil:u} ({retryDelay.TotalMinutes:F1} minutes).");
                    }

                    // Continue to next engine
                    continue;
                }
            }

            // All engines failed
            if (exceptions.Count == 0)
            {
                throw new AIEngineAPIException($"All engines are disabled/skipped.");
            }
            if (exceptions.Count == 1)
            {
                throw exceptions[0];
            }

            var allEnginesFailed = new AggregateException(
                $"All {Engines.Count} engines in the collection failed.",
                exceptions);
            throw new AIEngineAPIException(
                $"All engines failed. Errors: {string.Join("; ", exceptions.Select(e => e.Message))}",
                allEnginesFailed,
                AIEngineErrorType.Other);
        }

        private class SkippedEngineInfo
        {
            public AIEngineErrorType ErrorType { get; }
            public DateTime SkipUntil { get; }
            public string LastErrorMessage { get; }

            public SkippedEngineInfo(AIEngineErrorType errorType, DateTime skipUntil, string lastErrorMessage)
            {
                ErrorType = errorType;
                SkipUntil = skipUntil;
                LastErrorMessage = lastErrorMessage;
            }
        }
    }
}