using FunscriptToolbox.SubtitlesVerbs.Transcriptions;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbs.Translations
{
    internal class TranslatorAIGenericAPI : TranslatorAI
    {
        public const string ToolName = "GenericAI-API";

        public TranslatorAIGenericAPI()
        {
        }

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

        public override void Translate(
            SubtitleGeneratorContext context,
            Transcription transcription,
            Translation translation)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("user-agent", "Mozilla/5.0 (Windows NT 6.1) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/41.0.2228.0 Safari/537.36");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Content-Type", "application/json; charset=UTF-8");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
            client.BaseAddress = new Uri(this.BaseAddress);
            client.Timeout = this.TimeOut;
            if (this.APIKeyName != null)
            {
                client.DefaultRequestHeaders.Add("Authorization", "Bearer " + context.GetPrivateConfig(this.APIKeyName));
            }

            // Get only the items that have not been translated yet
            var items = this.MessagesHandler.GetAllItems(
                transcription,
                context.CurrentWipsub.SubtitlesForcedTiming);

            // Parse previous files, they might contains translations if the user fixed them
            var nbErrors = this.HandlePreviousFiles(
                context,
                transcription,
                translation,
                items,                
                $"-\\d+\\.txt");

            // If there are still translations to be done, create files for each batch of items
            if (nbErrors == 0)
            {
                var processStartTime = DateTime.Now;
                var lastTimeSaved = DateTime.Now;
                foreach (var request in this.MessagesHandler.CreateRequests(
                    transcription,
                    translation,
                    items))
                {
                    if (DebugNbRequestsLimit != null && request.Number > DebugNbRequestsLimit.Value)
                    {
                        context.WriteInfo($"    Stopping because DebugNbRequestsLimit reached ({DebugNbRequestsLimit.Value})");
                        return;
                    }

                    dynamic requestBody = request.Body;
                    if (this.Model != null)
                    {
                        requestBody.model = this.Model;
                    }
                    requestBody = Merge(request.Body, RequestBodyExtension);

                    var requestBodyAsJson = JsonConvert.SerializeObject(requestBody, Formatting.Indented);

                    var verbosePrefix = $"{transcription.Id}-{translation.Id}-{request.Number:D04}";
                    context.CreateVerboseFile($"{verbosePrefix}-Req.json", requestBodyAsJson, processStartTime);

                    try
                    {
                        var watch = Stopwatch.StartNew();
                        var response = client.PostAsync(
                            new Uri(
                                client.BaseAddress,
                                "/v1/chat/completions"),
                            new StringContent(
                                requestBodyAsJson,
                                Encoding.UTF8,
                                "application/json")
                            ).Result;
                        if (!response.IsSuccessStatusCode)
                        {
                            context.WriteError($"Error: {response.StatusCode} - {response.ReasonPhrase}");
                            return;
                        }

                        string responseAsJson = response.Content.ReadAsStringAsync().Result;
                        watch.Stop();

                        dynamic responseBody = JsonConvert.DeserializeObject(responseAsJson);
                        context.CreateVerboseFile($"{verbosePrefix}-Resp.json", JsonConvert.SerializeObject(responseBody, Formatting.Indented), processStartTime);

                        string assistantMessage = responseBody.choices[0].message.content;
                        translation.Costs.Add(
                            new TranslationCost(
                                $"{this.BaseAddress},{this.Model}",
                                watch.Elapsed,
                                request.Items.Length,
                                (int?)responseBody.usage?.prompt_tokens,
                                (int?)responseBody.usage?.completion_tokens));

                        if (ValidateModelNameInResponse && !string.Equals((string)responseBody.model, this.Model, StringComparison.OrdinalIgnoreCase))
                        {
                            context.WriteError($"Invalid model name in response:\n" +
                                $"   [API response] {responseBody.model}\n" +
                                $"   [config]   {this.Model}");
                            return;
                        }

                        var nbTranslationAdded = this.MessagesHandler.HandleResponse(
                            translation,
                            items,
                            assistantMessage,
                            request.Items);
                        if (DateTime.Now - lastTimeSaved > TimeSpan.FromMinutes(1))
                        {
                            context.CurrentWipsub.Save();
                            lastTimeSaved = DateTime.Now;
                        }

                        context.DefaultUpdateHandler(
                            ToolName, 
                            request.ToolAction, 
                            request.Items.LastOrDefault()?.Tag.TranslatedTexts.FirstOrDefault(f => f.Id == translation.Id)?.Text); // Display the last translated text
                    }
                    catch (AIMessagesHandlerExpection ex)
                    {
                        var filepath = $"{context.CurrentBaseFilePath}.TODO-{transcription.Id}-{translation.Id}-{request.Number:D04}.txt";
                        context.SoftDelete(filepath);
                        File.WriteAllText(filepath, ex.PartiallyFixedResponse, Encoding.UTF8);
                        context.WriteInfo($"Error while parsing response from the API: {ex.Message}");
                        context.AddUserTodo($"Manually fix the following error in file '{filepath}':\n{ex.Message}");
                        return;
                    }
                    catch (AggregateException ex)
                    {
                        var builder = new StringBuilder();
                        builder.AppendLine($"Error while communicating or parsing response from the API:");
                        foreach (var innerException in ex.InnerExceptions)
                        {
                            var currentException = innerException;
                            while (currentException.InnerException != null)
                            {
                                currentException = currentException.InnerException;
                            }
                            builder.AppendLine($"- {currentException.Message}");
                        }
                        context.WriteError(builder.ToString());
                        context.WriteLog(ex.ToString());
                        context.CurrentWipsub.Save();
                        return;
                    }
                    catch (Exception ex)
                    {
                        context.WriteError($"Error while communicating or parsing response from the API: {ex.Message}");
                        context.CurrentWipsub.Save();
                        return;
                    }
                }
            }
        }

        private static dynamic Merge(ExpandoObject item1, ExpandoObject item2)
        {
            if (item2 == null)
                return item1;

            var result = new ExpandoObject();
            var d = result as IDictionary<string, object>; //work with the Expando as a Dictionary

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