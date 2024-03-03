using System;
using System.Threading.Tasks;
using System.Net.Http;
using Newtonsoft.Json;

namespace FunscriptToolbox.SubtitlesVerbV2
{
    public class GoogleTranslate
    {
        private readonly HttpClient _client;

        public GoogleTranslate()
        {
            _client = new HttpClient();
            _client.DefaultRequestHeaders.Add("user-agent", "Mozilla/5.0 (Windows NT 6.1) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/41.0.2228.0 Safari/537.36");
            _client.DefaultRequestHeaders.TryAddWithoutValidation("Content-Type", "application/json; charset=UTF-8");
            _client.BaseAddress = new Uri("https://translate.googleapis.com/");
        }


        public string Translate(string text, string sourceLanguageCode, string targetLanguageCode)
        {
            return TranslateAsync(text, sourceLanguageCode, targetLanguageCode).Result;
        }

        public async Task<string> TranslateAsync(string text, string sourceLanguageCode, string targetLanguageCode)
        {
            string apiUrl = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl={sourceLanguageCode}&tl={targetLanguageCode}&dt=t&q={Uri.EscapeDataString(text)}";

            HttpResponseMessage response = await _client.GetAsync(apiUrl);
            if (response.IsSuccessStatusCode)
            {
                string jsonResponse = await response.Content.ReadAsStringAsync();
                dynamic result = JsonConvert.DeserializeObject(jsonResponse);
                return ExtractTranslatedText(result);
            }
            else
            {
                throw new HttpRequestException($"Error: {response.StatusCode} - {response.ReasonPhrase}");
            }
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
