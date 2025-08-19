using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.IO;
using System.Net.Http;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbs
{
    public sealed class AIEngineAPI : AIEngine
    {
        public const string ToolName = "GenericAI-API";

        [JsonProperty(Order = 10, Required = Required.Always)]
        public string BaseAddress { get; set; } = "http://localhost:10000";

        [JsonProperty(Order = 11)]
        public string Model { get; set; }

        [JsonProperty(Order = 12)]
        public bool ValidateModelNameInResponse { get; set; } = false;

        [JsonProperty(Order = 13)]
        public string APIKeyName { get; set; }

        [JsonProperty(Order = 14)]
        public TimeSpan TimeOut { get; set; } = TimeSpan.FromSeconds(300);

        [JsonProperty(Order = 15, TypeNameHandling = TypeNameHandling.None)]
        public ExpandoObject RequestBodyExtension { get; set; }

        [JsonProperty(Order = 16)]
        public int? DebugNbRequestsLimit { get; set; } = null;

        public override bool Execute(
            SubtitleGeneratorContext context,
            IEnumerable<AIRequest> requests)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("user-agent", "Mozilla/5.0 (Windows NT 6.1) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/41.0.2228.0 Safari/537.36");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Content-Type", "application/json; charset=UTF-8");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
            client.BaseAddress = new Uri(this.BaseAddress.EndsWith("/") ? this.BaseAddress : this.BaseAddress + "/");
            client.Timeout = this.TimeOut;

            if (this.APIKeyName != null)
            {
                client.DefaultRequestHeaders.Add("Authorization", "Bearer " + context.GetPrivateConfig(this.APIKeyName));
            }

            var processStartTime = DateTime.Now;
            var lastTimeSaved = DateTime.Now;

            foreach (var request in requests)
            {
                if (DebugNbRequestsLimit != null && request.Number > DebugNbRequestsLimit.Value)
                {
                    context.WriteInfo($"    Stopping because DebugNbRequestsLimit reached ({DebugNbRequestsLimit.Value})");
                    return false;
                }

                dynamic requestBody = new ExpandoObject();
                if (this.Model != null)
                {
                    requestBody.model = this.Model;
                }
                requestBody = Merge(requestBody, RequestBodyExtension);
                requestBody.messages = request.Messages;

                var requestBodyAsJson = JsonConvert.SerializeObject(requestBody, Formatting.Indented);

                var verbosePrefix = request.GetVerbosePrefix();
                context.CreateVerboseFile($"{verbosePrefix}-Req.txt", request.FullPrompt, processStartTime);
                context.CreateVerboseFile($"{verbosePrefix}-Req.json", requestBodyAsJson, processStartTime);

                try
                {
                    var watch = Stopwatch.StartNew();
                    var response = client.PostAsync(
                        new Uri(client.BaseAddress, "chat/completions"),
                        new StringContent(requestBodyAsJson, Encoding.UTF8, "application/json")
                    ).Result;

                    if (!response.IsSuccessStatusCode)
                    {
                        context.WriteError($"Error: {response.StatusCode} - {response.ReasonPhrase}");
                        return false;
                    }

                    string responseAsJson = response.Content.ReadAsStringAsync().Result;
                    watch.Stop();

                    dynamic responseBody = JsonConvert.DeserializeObject(responseAsJson);
                    context.CreateVerboseFile($"{verbosePrefix}-Resp.json", JsonConvert.SerializeObject(responseBody, Formatting.Indented), processStartTime);

                    if (ValidateModelNameInResponse && !string.Equals((string)responseBody.model, this.Model, StringComparison.OrdinalIgnoreCase))
                    {
                        context.WriteError($"Invalid model name in response:\n" +
                            $"   [API response] {responseBody.model}\n" +
                            $"   [config]   {this.Model}");
                        return false;
                    }

                    string assistantMessage = responseBody.choices[0].message.content;
                    context.CreateVerboseFile($"{verbosePrefix}-Resp.txt", assistantMessage, processStartTime);
                    if (assistantMessage == null)
                    {
                        context.WriteError($"No message return by the AI");
                        return false;
                    }

                    // Let the request handle the response and store the api cost
                    request.HandleResponse(
                        context,
                        $"{this.BaseAddress},{this.Model}",
                        watch.Elapsed,
                        assistantMessage,
                        (int?)responseBody?.usage?.prompt_tokens,
                        (int?)responseBody?.usage?.completion_tokens,
                        (int?)responseBody?.usage?.total_tokens);

                    if (DateTime.Now - lastTimeSaved > TimeSpan.FromMinutes(1))
                    {
                        context.CurrentWipsub.Save();
                        lastTimeSaved = DateTime.Now;
                    }
                }
                catch (AIEngineException ex)
                {
                    var filepath = request.GetFilenamePattern(context.CurrentBaseFilePath);
                    context.SoftDelete(filepath);
                    File.WriteAllText(filepath, $"{ex.Message.Replace("[", "(").Replace("]", ")")}\n\n{ex.PartiallyFixedResponse}", Encoding.UTF8);
                    context.WriteInfo($"Error while parsing response from the API: {ex.Message}");
                    context.AddUserTodo($"Manually fix the following error in file '{Path.GetFileName(filepath)}':\n{ex.Message}");
                    return false;
                }
                catch (Exception ex) when (ex is AggregateException || ex is HttpRequestException)
                {
                    context.WriteError($"Error while communicating with the API: {ex.Message}");
                    context.WriteLog(ex.ToString());
                    context.CurrentWipsub.Save();
                    return false;
                }
            }
            return true;
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
    }
}