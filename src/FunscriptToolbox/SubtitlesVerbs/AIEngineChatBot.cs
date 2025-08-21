using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbs
{
    public sealed class AIEngineChatBot : AIEngine
    {
        public override void Execute(
            SubtitleGeneratorContext context,
            IEnumerable<AIRequest> requests)
        {
            foreach (var request in requests)
            {
                var filepath = request.GetFilenamePattern(context.CurrentBaseFilePath);

                context.WriteInfo($"        Creating file '{Path.GetFileName(filepath)}' (contains {request.NbItemsString()})...");
                context.SoftDelete(filepath);
                File.WriteAllText(filepath, request.FullPrompt, Encoding.UTF8);

                context.AddUserTodo($"Feed the content of '{Path.GetFileName(filepath)}' to an AI, then replace the content of the file with the AI's answer.");
            }
        }
    }
}