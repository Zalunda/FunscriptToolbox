using System.IO;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public sealed class AIEngineChatBot : AIEngine
    {
        public override AIResponse Execute(
            SubtitleGeneratorContext context,
            AIRequest request)
        {
            var filepath = request.GetFilenamePattern(context.WIP.BaseFilePath);

            context.WriteInfo($"        Creating file '{Path.GetFileName(filepath)}'...");
            context.SoftDelete(filepath);
            File.WriteAllText(filepath, request.FullPrompt, Encoding.UTF8);

            context.AddUserTodo($"Feed the content of '{Path.GetFileName(filepath)}' to an AI, then replace the content of the file with the AI's answer.");
            return new AIResponse(request, null, null);
        }
    }
}