using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.Transcriptions;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerbs.Translations
{
    public abstract class AIEngine
    {
        public abstract void Execute(
            SubtitleGeneratorContext context, 
            AIMessagesHandler messagesHandler, 
            Transcription transcription, 
            Translation translation);

        protected int HandlePreviousFiles(
            SubtitleGeneratorContext context,
            AIMessagesHandler messagesHandler,
            Transcription transcription,
            Translation translation,
            AIMessagesHandler.ItemForAICollection items,
            string patternSuffixe)
        {
            var nbErrors = 0;
            foreach (var fullpath in Directory.GetFiles(
                PathExtension.SafeGetDirectoryName(context.CurrentBaseFilePath),
                "*.*"))
            {
                var filename = Path.GetFileName(fullpath);
                if (Regex.IsMatch(
                    filename,
                    $"^" + Regex.Escape($"{Path.GetFileName(context.CurrentBaseFilePath)}.TODO-{transcription.Id}-{translation.Id}") + $"{patternSuffixe}$",
                    RegexOptions.IgnoreCase))
                {
                    var response = File.ReadAllText(fullpath);
                    context.SoftDelete(fullpath);
                    try
                    {
                        context.WriteInfo($"        Analysing existing file '{filename}'...");
                        var nbTranslationsAdded = messagesHandler.HandleResponse(
                            translation,
                            items,
                            response);
                        context.WriteInfo($"        Finished:");
                        context.WriteInfo($"            Nb translations added: {nbTranslationsAdded}");
                        if (nbTranslationsAdded > 0)
                        {
                            context.CurrentWipsub.Save();
                        }
                    }
                    catch (AIMessagesHandlerExpection ex)
                    {
                        nbErrors++;
                        File.WriteAllText(fullpath, $"{ex.Message.Replace("[", "(").Replace("]", ")")}\n\n{ex.PartiallyFixedResponse}", Encoding.UTF8);
                        context.WriteInfo($"Error while parsing file '{filename}':{ex.Message}");
                        context.AddUserTodo($"Manually fix the following error in file '{filename}':\n{ex.Message}");
                    }
                }
            }

            return nbErrors;
        }

    }
}