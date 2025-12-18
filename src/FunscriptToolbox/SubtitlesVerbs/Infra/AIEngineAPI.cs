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

        public double GetInputCost(int nbTokens) => (double)nbTokens / 1_000_000 * EstimatedCostPerInputMillionTokens;
        public double GetOutputCost(int nbTokens) => (double)nbTokens / 1_000_000 * EstimatedCostPerOutputMillionTokens;

        [JsonProperty(Order = 20)]
        public bool PauseBeforeSendingRequest { get; set; } = false;
        [JsonProperty(Order = 21)]
        public bool PauseBeforeSavingResponse { get; set; } = false;

        [JsonIgnore]
        public string EngineIdentifierWithoutModel => $"{BaseAddress}|{APIKeyName}";
        [JsonIgnore]
        public string EngineIdentifier => $"{EngineIdentifierWithoutModel}|{Model}";

        protected dynamic CreateRequestBodyWithExtension()
        {
            dynamic requestBody = new ExpandoObject();
            requestBody = Merge(requestBody, this.RequestBodyExtension);
            return requestBody;
        }

        private static dynamic Merge(ExpandoObject item1, ExpandoObject item2)
        {
            if (item2 == null)
                return item1;

            var result = new ExpandoObject();
            var d = result as IDictionary<string, object>;

            foreach (var pair in (IDictionary<string, object>)item1)
            {
                d[pair.Key] = pair.Value;
            }
            foreach (var pair in (IDictionary<string, object>)item2)
            {
                d[pair.Key] = pair.Value;
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