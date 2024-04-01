using FunscriptToolbox.SubtitlesVerbsV2.Transcriptions;
using FunscriptToolbox.SubtitlesVerbV2;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerbsV2.Translations
{
    internal class TranslatorChatBotAI : Translator
    {
        public TranslatorChatBotAI()
        {
        }

        [JsonProperty(Order = 10)]
        public AIMessagesHandler MessagesHandler { get; set; }

        public override void Translate(
            SubtitleGeneratorContext context,
            string baseFilePath,
            Transcription transcription,
            Translation translation)
        {
            // TODO Add info/verbose/user-todo logs

            var items = this.MessagesHandler.GetAllItems(transcription, context.Wipsub.SubtitlesForcedTiming, this.TranslationId);
            if (items.Length == 0)
            {
                return;
            }
            foreach (var fullpath in Directory.GetFiles(Path.GetDirectoryName(baseFilePath) ?? ".", Path.GetFileName(baseFilePath) + "*.*"))
            {
                try
                {
                    var filename = Path.GetFileName(fullpath);
                    var baseFilename = Path.GetFileName(baseFilePath);
                    if (Regex.IsMatch(filename, $"^{baseFilename}.{transcription.Id}-{translation.Id}-BATCH-\\d+\\.txt", RegexOptions.IgnoreCase))
                    {
                        this.MessagesHandler.HandleResponse(
                            this.TranslationId,
                            items,
                            File.ReadAllText(fullpath));
                        context.SoftDelete(fullpath);
                    }
                }
                catch (Exception ex)
                {
                    context.WriteError(ex.ToString());
                    context.Wipsub.Save();
                    return;
                }
            }

            var itemsAfter = this.MessagesHandler.GetAllItems(transcription, context.Wipsub.SubtitlesForcedTiming, this.TranslationId);
            context.Wipsub.Save();

            foreach (var request in this.MessagesHandler.CreateRequests(
                this.TranslationId,
                items,
                transcription.Language,
                translation.Language))
            {
                File.WriteAllText(
                    $"{baseFilePath}.{transcription.Id}-{translation.Id}-BATCH-{request.Number:D03}.txt",
                    request.FullPrompt);
            }
        }
    }
}