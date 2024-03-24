using FunscriptToolbox.SubtitlesVerbsV2.Transcriptions;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.Net.Http;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbsV2.Translations
{
    internal class GenericOpenAIAPITranslator : AITranslator
    {
        public GenericOpenAIAPITranslator(
            string translationId,
            string baseAddress = "http://localhost:10000")
            : base(translationId)
        {
            BaseAddress = baseAddress;
        }

        public string BaseAddress { get; set; }
        public string Model { get; set; }
        public string APIKey { get; set; } // TODO Should be in a different file
        public TimeSpan TimeOut { get; set; } = TimeSpan.FromSeconds(300);

        [JsonProperty(TypeNameHandling = TypeNameHandling.None)]
        public ExpandoObject DataExpansion { get; set; }

        public override void Translate(
            string baseFilePath,
            Transcription transcription,
            Translation translation,
            Action saveAction)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("user-agent", "Mozilla/5.0 (Windows NT 6.1) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/41.0.2228.0 Safari/537.36");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Content-Type", "application/json; charset=UTF-8");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
            client.BaseAddress = new Uri(this.BaseAddress);
            client.Timeout = this.TimeOut;
            if (this.APIKey != null)
            {
                client.DefaultRequestHeaders.Add("Authorization", "Bearer " + this.APIKey);
            }

            var items = GetAllItems(transcription);
            foreach (var request in CreateRequests(
                items,
                transcription.Language,
                translation.Language))
            {
                dynamic data = new ExpandoObject();
                if (this.Model != null)
                {
                    data.model = this.Model;
                }

                var messages = new List<dynamic>();
                if (SystemPrompt != null)
                {
                    messages.Add(
                        new { 
                            role = "system", 
                            content = ConvertPromptLinesToPrompt(
                                SystemPrompt, 
                                transcription.Language, 
                                translation.Language) });
                }
                messages.Add(
                    new { 
                        role = "user", 
                        content = request.Content });
                data.messages = messages;

                data = Merge(data, DataExpansion);

                var json = JsonConvert.SerializeObject(data);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                Console.WriteLine($"--------------------------------");
                Console.WriteLine(request.Content);

                var watch = Stopwatch.StartNew();
                var response = client.PostAsync(
                    new Uri(client.BaseAddress, "/v1/chat/completions"), 
                    content).Result;
                string responseContent = response.Content.ReadAsStringAsync().Result;
                watch.Stop();

                dynamic responseObject = JsonConvert.DeserializeObject(responseContent);
                string assistantMessage = responseObject.choices[0].message.content;
                translation.Costs.Add(
                    new TranslationCost(
                        $"{this.BaseAddress},{this.Model}",
                        request.Items.Length,
                        watch.Elapsed,
                        (int?)responseObject.usage?.prompt_tokens,
                        (int?)responseObject.usage?.completion_tokens));
                HandleResponse(request, assistantMessage);
                saveAction();
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