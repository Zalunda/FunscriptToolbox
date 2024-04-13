using FunscriptToolbox.SubtitlesVerbsV2.Transcriptions;
using System.IO;

namespace FunscriptToolbox.SubtitlesVerbsV2.Translations
{
    internal class TranslatorAIChatBot : TranslatorAI
    {
        public TranslatorAIChatBot()
        {
        }

        public override void Translate(
            SubtitleGeneratorContext context,
            Transcription transcription,
            Translation translation)
        {
            // Get only the items that have not been translated yet
            var items = this.MessagesHandler.GetAllItems(
                transcription,
                context.CurrentWipsub.SubtitlesForcedTiming);

            // Parse previous files, they might contains translations if the user updated them
            var nbErrors = this.HandlePreviousFiles(
                context,
                transcription,
                translation,
                items,
                "-\\d+\\.txt");

            // If there are still translations to be done, create files for each batch of items
            if (nbErrors == 0)
            {
                foreach (var request in this.MessagesHandler.CreateRequests(
                    transcription,
                    translation,
                    items))
                {
                    var filepath = $"{context.CurrentBaseFilePath}.TODO-{transcription.Id}-{translation.Id}-{request.Number:D04}.txt";
                    context.WriteInfo($"        Creating file '{Path.GetFileName(filepath)}' (contains {request.Items.Length} texts)...");
                    context.SoftDelete(filepath);
                    File.WriteAllText(filepath, request.FullPrompt);

                    context.AddUserTodo($"Feed the content of '{Path.GetFileName(filepath)}' to an AI, then replace the content of the file with the AI's answer.");
                }
            }
        }
    }
}