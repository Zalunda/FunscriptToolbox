using FunscriptToolbox.Core.Infra;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public abstract class AIEngineAPI : AIEngine
    {
        [JsonIgnore]
        public abstract string ToolName { get; }

        public bool IsAPIKeyAvailableIfNeeded(SubtitleGeneratorContext context) => (this.APIKeyName == null || context.GetPrivateConfig(this.APIKeyName) != null);

        [JsonProperty(Order = 10, Required = Required.Always)]
        public string BaseAddress { get; set; } = "http://localhost:10000";

        [JsonProperty(Order = 11, Required = Required.Always)]
        public string Model { get; set; }

        [JsonProperty(Order = 12)]
        public bool ValidateModelNameInResponse { get; set; } = false;

        [JsonProperty(Order = 13)]
        public string APIKeyName { get; set; }

        [JsonProperty(Order = 14, TypeNameHandling = TypeNameHandling.None)]
        public ExpandoObject RequestBodyExtension { get; set; }

        [JsonProperty(Order = 15)]
        public TimeSpan TimeOut { get; set; } = TimeSpan.FromMinutes(15);

        [JsonProperty(Order = 16)]
        public double EstimatedCostPerInputMillionTokens { get; set; } = 0.0;

        [JsonProperty(Order = 17)]
        public double EstimatedCostPerOutputMillionTokens { get; set; } = 0.0;

        public double GetInputCost(int nbTokens) => (double)nbTokens / 1_000_000 * EstimatedCostPerInputMillionTokens * CostSaving;
        public double GetOutputCost(int nbTokens) => (double)nbTokens / 1_000_000 * EstimatedCostPerOutputMillionTokens * CostSaving;

        [JsonIgnore]
        protected virtual double CostSaving => 1.0;

        [JsonProperty(Order = 20)]
        public bool PauseBeforeSendingRequest { get; set; } = false;
        [JsonProperty(Order = 21)]
        public bool PauseBeforeSavingResponse { get; set; } = false;

        [JsonIgnore]
        public string EngineIdentifierWithoutModel => $"{BaseAddress}|{APIKeyName}";
        [JsonIgnore]
        public virtual string EngineIdentifier => $"{EngineIdentifierWithoutModel}|{Model}";

        protected dynamic CreateRequestBodyWithExtension(AIEngineConfigOverride configOverride)
        {
            dynamic requestBody = new ExpandoObject();
            requestBody = Merge(requestBody, this.RequestBodyExtension);

            if (configOverride?.RequestBodyExtension != null)
            {
                requestBody = Merge(requestBody, configOverride.RequestBodyExtension);
            }

            return requestBody;
        }

        private static dynamic Merge(ExpandoObject item1, ExpandoObject item2)
        {
            if (item2 == null) return item1 ?? new ExpandoObject();
            if (item1 == null) return item2;

            var result = new ExpandoObject();
            var d = result as IDictionary<string, object>;

            foreach (var pair in (IDictionary<string, object>)item1)
            {
                d[pair.Key] = pair.Value;
            }
            foreach (var pair in (IDictionary<string, object>)item2)
            {
                // Recursive merge for nested objects (e.g. generationConfig)
                if (d.TryGetValue(pair.Key, out var existingVal)
                    && existingVal is ExpandoObject expando1
                    && pair.Value is ExpandoObject expando2)
                {
                    d[pair.Key] = Merge(expando1, expando2);
                }
                else
                {
                    d[pair.Key] = pair.Value;
                }
            }

            return result;
        }

        private static Regex rs_timeRegex = new Regex(@"(?<Grab>\""(StartTime|EndTime)\"":\s*\""(?<Time>[^\""]*))\""", RegexOptions.Compiled);

        protected string AddRealTime(SubtitleGeneratorContext context, TimeSpan startOffset, string assistantMessage)
        {
            return rs_timeRegex.Replace(
                assistantMessage,
                match =>
                {
                    try
                    {
                        var grab = match.Groups["Grab"].Value;
                        var requestSpecificTime = TimeSpanExtensions.FlexibleTimeSpanParse(
                            match.Groups["Time"].Value);
                        var globalTime = startOffset + requestSpecificTime;
                        return context.WIP.TimelineMap.IsMultipart
                            ? $"{grab} [{context.WIP.TimelineMap.ConvertToPartSpecificFileIndexAndTime(globalTime)}]\""
                            : $"{grab}\"";
                    }
                    catch (Exception)
                    {
                        return match.Value;
                    }
                });
        }

        protected void PauseIfEnabled(bool isEnabled, string content = null)
        {
            if (isEnabled)
            {
                if (content != null)
                {
                    Console.WriteLine("\n\n" + content);
                }
                Console.WriteLine("\n\nPress Q to stop this worker. Any other key to continue.");
                var keyInfo = Console.ReadKey(true);
                if (keyInfo.Key == ConsoleKey.Q)
                {
                    throw new UserStoppedWorkerException();
                }
            }
        }

    }
}