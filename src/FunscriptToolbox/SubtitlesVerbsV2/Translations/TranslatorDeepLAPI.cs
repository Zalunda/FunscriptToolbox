using System;
using System.Net.Http;
using Newtonsoft.Json;
using System.Linq;
using FunscriptToolbox.SubtitlesVerbsV2.Transcriptions;
using System.Diagnostics;
using FunscriptToolbox.SubtitlesVerbV2;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbsV2.Translations
{
    internal class TranslatorDeepLAPI : Translator
    {
        private const string ToolName = "DeepL-API";

        public TranslatorDeepLAPI()
        {
        }

        [JsonProperty(Order = 10)]
        public string BaseAddress { get; set; } = "https://api-free.deepl.com";

        [JsonProperty(Order = 11)]
        public string APIKeyName { get; set; } = "APIKeyDeepL";

        // See: https://developers.deepl.com/docs/api-reference/translate/openapi-spec-for-text-translation
        [JsonProperty(Order = 12)]
        public string Formalilty { get; set; } = "prefer_less";


        [JsonProperty(Order = 12)]
        public int NbPerRequest { get; set; } = 50;

        public override void Translate(
            SubtitleGeneratorContext context,
            Transcription transcription,
            Translation translation)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("user-agent", "Mozilla/5.0 (Windows NT 6.1) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/41.0.2228.0 Safari/537.36");
            client.DefaultRequestHeaders.Add("Authorization", $"DeepL-Auth-Key {context.GetPrivateConfig(this.APIKeyName)}");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Content-Type", "application/json; charset=UTF-8");
            client.BaseAddress = new Uri(this.BaseAddress);

            var missingTranscriptions = transcription
                .Items
                .Where(f => !f.TranslatedTexts.Any(t => t.Id == translation.Id))
                .ToArray();

            var currentStartIndex = 0;
            while (currentStartIndex < missingTranscriptions.Length)
            {
                var batch = missingTranscriptions
                    .Skip(currentStartIndex)
                    .Take(NbPerRequest)
                    .ToArray();

                var requestBody = new
                {
                    text = batch.Select(f => f.Text).ToArray(),
                    source_lang = transcription.Language.ShortName,
                    target_lang = translation.Language.ShortName,
                    formality = this.Formalilty
                };
                string requestBodyAsJson = JsonConvert.SerializeObject(requestBody);

                var watch = Stopwatch.StartNew();
                var response = client.PostAsync(
                    new Uri(
                        client.BaseAddress, 
                        "/v2/translate"),
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

                translation.Costs.Add(
                    new TranslationCost(ToolName, watch.Elapsed, batch.Length));

                dynamic responseBody = JsonConvert.DeserializeObject(responseAsJson);
                var index = 0;
                foreach (var translationItem in responseBody.translations)
                {
                    var translatedText = (string) translationItem.text;
                    batch[index].TranslatedTexts.Add(
                        new TranslatedText(translation.Id, translatedText));
                    index++;
                    context.DefaultUpdateHandler(ToolName, $"{currentStartIndex + index}/{missingTranscriptions.Length}", translatedText);
                }

                currentStartIndex += NbPerRequest;
            }
        }
    }
}
