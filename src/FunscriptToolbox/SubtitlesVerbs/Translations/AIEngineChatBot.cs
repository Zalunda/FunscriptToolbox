using FunscriptToolbox.SubtitlesVerbs.Transcriptions;
using System.IO;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbs.Translations
{
    public sealed class AIEngineChatBot : AIEngine
    {
        public override void Execute(
            SubtitleGeneratorContext context,
            AIMessagesHandler messagesHandler,
            Transcription transcription,
            Translation translation)
        {
            // Get only the items that have not been translated yet
            var items = messagesHandler.GetAllItems(
                transcription,
                context.CurrentWipsub.SubtitlesForcedTiming);

            // Parse previous files, they might contain translations if the user updated them
            var nbErrors = base.HandlePreviousFiles(
                context,
                messagesHandler,
                transcription,
                translation,
                items,
                "-\\d+\\.txt");

            // If there are still translations to be done, create files for each batch of items
            if (nbErrors == 0)
            {
                foreach (var request in messagesHandler.CreateRequests(
                    transcription,
                    translation,
                    items))
                {
                    var filepath = $"{context.CurrentBaseFilePath}.TODO-{transcription.Id}-{translation.Id}-{request.Number:D04}.txt";
                    context.WriteInfo($"        Creating file '{Path.GetFileName(filepath)}' (contains {request.Items.Length} texts)...");
                    context.SoftDelete(filepath);
                    File.WriteAllText(filepath, request.FullPrompt, Encoding.UTF8);

                    context.AddUserTodo($"Feed the content of '{Path.GetFileName(filepath)}' to an AI, then replace the content of the file with the AI's answer.");
                }
            }
        }
    }
}