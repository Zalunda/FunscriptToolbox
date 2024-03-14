using FunscriptToolbox.SubtitlesVerbsV2.Transcription;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerbsV2.Translation
{
    internal class AITranslator : ITranslator
    {
        public AITranslator()
        {
        }

        public void Translate(
            string translationId, 
            FullTranscription transcription, 
            string sourceLanguage, 
            string targetLanguage, 
            Action saveAction)
        {
            // Reference: https://github.com/oobabooga/text-generation-webui/wiki/12-%E2%80%90-OpenAI-API

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("user-agent", "Mozilla/5.0 (Windows NT 6.1) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/41.0.2228.0 Safari/537.36");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Content-Type", "application/json; charset=UTF-8");
            client.BaseAddress = new Uri("http://127.0.0.1:10000/v1/chat/completions");
            client.Timeout = TimeSpan.FromSeconds(600);

            var characterName = "Translator";
            var maxPrevious = 50;

            var allTexts = transcription.Items.Select(f => f.Text).ToArray();
            // TODO Load model with /v1/engines/{model_name} maybe??

            var previousTranslation = new List<Tuple<string, string>>();
            previousTranslation.Add(new Tuple<string, string>("もしもし、", "Hello,"));
            previousTranslation.Add(new Tuple<string, string>("ごめんね。", "I'm sorry,"));
            previousTranslation.Add(new Tuple<string, string>("急に電話かけちゃって。", "I suddenly called you."));
            // TODO: Add more "perfect" example

            // TheBloke_Mixtral-8x7B-Instruct-v0.1-GPTQ, Translator, 150 characters to generate
            for (int i = 0; i < allTexts.Length; i++)
            {
                var alreadyTranslated = transcription.Items[i].Translations
                    .FirstOrDefault(t => t.Id == translationId)?.Text;
                if (alreadyTranslated != null)
                {
                    previousTranslation.Add(new Tuple<string, string>(transcription.Items[i].Text, alreadyTranslated));
                    continue;
                }

                while (previousTranslation.Count > maxPrevious)
                {
                    previousTranslation.RemoveAt(0);
                }

                var messages = new List<dynamic>();
                var text = allTexts[i];
                var m = new StringBuilder();
                if (characterName == null)
                {
                    messages.Add(new { role = "system", content = $"Task: Translate porn movie subtitles from {sourceLanguage} to {targetLanguage} based on the instructions\n\nObjective:\n\nTranslate subtitles accurately while maintaining the original meaning and tone of the movie.\n\nUse natural-sounding {targetLanguage} phrases and idioms that accurately convey the meaning of the original text.\n\nRoles:\n\nLinguist: Responsible for translating the subtitles from {sourceLanguage} to {targetLanguage}\n\nStrategy:\n\nTranslate subtitles accurately while maintaining the original meaning and tone of the scene.\n\nInstructions:\n\nUser Inputs any language of subtitles they want to translate one at a time.\n\nDetect the Language and Essence of the text.\n\nGenerate natural-sounding {targetLanguage} translations that accurately convey the meaning of the original text.\n\nCheck the accuracy and naturalness of the translations before submitting them to the user.\n\nThe audience for the translation are adults so it is acceptable to use explicitly sexual words or concept.\n\nThe subtitles are taken from VR scene where only the girl is talking." });
                }
                foreach (var previous in previousTranslation)
                {
                    m.AppendLine($"{sourceLanguage}: {previous.Item1}");
                    m.AppendLine($"{targetLanguage}: {previous.Item2}");
                    messages.Add(new { role = "user", content = $"{sourceLanguage}: {previous.Item1}" });
                    messages.Add(new { role = "assistant", content = $"{targetLanguage}: {previous.Item2}" });
                }

                m.AppendLine($"{sourceLanguage}: {text}");
                messages.Add(new { role = "user", content = $"{targetLanguage}: {text}" });

                dynamic data = new ExpandoObject();
                data.mode = "chat";
                data.messages = messages;
                if (characterName != null)
                {
                    data.character = characterName;
                }

                var json = JsonConvert.SerializeObject(data);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                Console.WriteLine($"------------- {i}/{allTexts.Length} -------------------");
                Console.WriteLine(text);

                var response = client.PostAsync(client.BaseAddress, content).Result;
                string responseContent = response.Content.ReadAsStringAsync().Result;

                dynamic responseObject = JsonConvert.DeserializeObject(responseContent);
                string assistantMessage = responseObject.choices[0].message.content;
                assistantMessage = assistantMessage.Replace($"{targetLanguage}: ", string.Empty);
                var firstLine = Regex.Match(assistantMessage, @"^(?<FirstLine>.*?)(\r|\n|$)").Groups["FirstLine"].Value;
                previousTranslation.Add(new Tuple<string, string>(text, firstLine));

                Console.WriteLine(assistantMessage);
                transcription.Items[i].Translations.Add(
                    new TranslatedText(translationId, assistantMessage));
                saveAction();
            }
        }
    }
}