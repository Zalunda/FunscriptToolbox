using FunscriptToolbox.SubtitlesVerbsV2.Transcriptions;
using FunscriptToolbox.SubtitlesVerbV2;
using Newtonsoft.Json;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerbsV2.Translations
{
    internal class TranslatorChatBotAI : Translator
    {
        public TranslatorChatBotAI()
        {
        }

        [JsonProperty(Order = 10, Required = Required.Always)]
        public AIMessagesHandler MessagesHandler { get; set; }

        public override void Translate(
            SubtitleGeneratorContext context,
            string baseFilePath,
            Transcription transcription,
            Translation translation)
        {
            // Get only the items that have not been translated yet
            var items = this.MessagesHandler.GetAllItems(
                transcription, 
                context.Wipsub.SubtitlesForcedTiming,
                this.TranslationId);

            // Parse previous files, they might contains translations if the user updated them
            var nbErrors = 0;
            foreach (var fullpath in Directory.GetFiles(Path.GetDirectoryName(baseFilePath) ?? ".", Path.GetFileName(baseFilePath) + "*.*"))
            {
                var filename = Path.GetFileName(fullpath);
                var baseFilename = Path.GetFileName(baseFilePath);
                if (Regex.IsMatch(filename, $"^{baseFilename}.{transcription.Id}-{translation.Id}-BATCH-\\d+\\.txt", RegexOptions.IgnoreCase))
                {
                    var response = File.ReadAllText(fullpath);
                    context.SoftDelete(fullpath);
                    try
                    {
                        context.WriteInfo($"        Analysing existing file '{filename}'...");
                        var nbTranslationsAdded = this.MessagesHandler.HandleResponse(
                            this.TranslationId,
                            items,
                            response);
                        context.WriteInfo($"        Finished:");
                        context.WriteInfo($"            Nb translations added: {nbTranslationsAdded}");
                        if (nbTranslationsAdded > 0)
                        {
                            context.Wipsub.Save();
                        }
                    }
                    catch (AIMessagesHandlerExpection ex)
                    {
                        nbErrors++;
                        File.WriteAllText(fullpath, ex.PartiallyFixedResponse, Encoding.UTF8);
                        context.WriteInfo($"Error while parsing file '{filename}':{ex.Message}");
                        context.AddUserTodo($"Manually fix the following error in file '{filename}':\n{ex.Message}");
                    }
                }
            }

            // If there are still translations to be done, create files for each batch of items
            if (nbErrors == 0)
            {
                foreach (var request in this.MessagesHandler.CreateRequests(
                    this.TranslationId,
                    items,
                    transcription.Language,
                    translation.Language))
                {
                    var filepath = $"{baseFilePath}.{transcription.Id}-{translation.Id}-BATCH-{request.Number:D03}.txt";
                    context.WriteInfo($"        Creating file '{Path.GetFileName(filepath)}' (contains {request.Items.Length} texts)...");
                    File.WriteAllText(filepath, request.FullPrompt);

                    context.AddUserTodo($"Give the content of the file '{Path.GetFileName(filepath)}' to an AI and then put its response in the same file.");
                }
            }
        }
    }
}