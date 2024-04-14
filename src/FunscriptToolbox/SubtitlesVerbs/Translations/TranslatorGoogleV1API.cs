using FunscriptToolbox.SubtitlesVerbs.Transcriptions;
using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;

namespace FunscriptToolbox.SubtitlesVerbs.Translations
{
    internal class TranslatorGoogleV1API : Translator
    {
        private const string ToolName = "GoogleV1-API";

        public TranslatorGoogleV1API()
        {
        }

        public override void Translate(
            SubtitleGeneratorContext context,
            Transcription transcription,
            Translation translation)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("user-agent", "Mozilla/5.0 (Windows NT 6.1) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/41.0.2228.0 Safari/537.36");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Content-Type", "application/json; charset=UTF-8");
            client.BaseAddress = new Uri("https://translate.googleapis.com/");

            var missingTranscriptions = transcription
                .Items
                .Where(f => !f.TranslatedTexts.Any(t => t.Id == translation.Id))
                .ToArray();

            var currentIndex = 1;
            foreach (var transcribedText in missingTranscriptions)
            {
                string apiUrl = $"https://translate.googleapis.com/translate_a/single" + 
                    "?client=gtx" + 
                    $"&sl={transcription.Language.ShortName}" +
                    $"&tl={translation.Language.ShortName}" + 
                    $"&dt=t" + 
                    $"&q={Uri.EscapeDataString(transcribedText.Text)}";

                var watch = Stopwatch.StartNew();
                var response = client.GetAsync(apiUrl).Result;
                if (!response.IsSuccessStatusCode)
                {
                    context.WriteError($"Error: {response.StatusCode} - {response.ReasonPhrase}");
                    return;
                }

                string responseAsJson = response.Content.ReadAsStringAsync().Result;
                watch.Stop();

                translation.Costs.Add(
                    new TranslationCost(ToolName, watch.Elapsed, 1));

                dynamic responseBody = JsonConvert.DeserializeObject(responseAsJson);

                var translatedText = (string)ExtractTranslatedText(responseBody);
                transcribedText.TranslatedTexts.Add(
                    new TranslatedText(translation.Id, translatedText));

                context.DefaultUpdateHandler(ToolName, $"{currentIndex++}/{missingTranscriptions.Length}", translatedText);
            }
        }

        public string SimpleTranslate(
            string originalText, 
            string shortOriginalLanguage,
            string shortTranslationLanguage)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("user-agent", "Mozilla/5.0 (Windows NT 6.1) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/41.0.2228.0 Safari/537.36");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Content-Type", "application/json; charset=UTF-8");
            client.BaseAddress = new Uri("https://translate.googleapis.com/");

            string apiUrl = $"https://translate.googleapis.com/translate_a/single" +
                "?client=gtx" +
                $"&sl={shortOriginalLanguage}" +
                $"&tl={shortTranslationLanguage}" +
                $"&dt=t" +
                $"&q={Uri.EscapeDataString(originalText)}";

            var response = client.GetAsync(apiUrl).Result;
            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Error: {response.StatusCode} - {response.ReasonPhrase}");
            }

            string responseAsJson = response.Content.ReadAsStringAsync().Result;
            dynamic responseBody = JsonConvert.DeserializeObject(responseAsJson);

            return (string)ExtractTranslatedText(responseBody);
        }

        private string ExtractTranslatedText(dynamic result)
        {
            var translatedText = "";
            foreach (var item in result[0])
            {
                if (translatedText.Length > 0)
                    translatedText += " ";
                translatedText += item[0].Value;
            }
            return translatedText;
        }
    }
}
