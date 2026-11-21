using FunscriptToolbox.SubtitlesVerbs.Progress;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public static class JsonCorrectionHelper
    {
        /// <summary>
        /// Prompts the user (in the web UI) to manually correct invalid JSON: Retry with the changes, Set Empty ([]), or Cancel (null).
        /// </summary>
        public static string PromptForFix(SubtitleGeneratorContext context, string errorMessage, string jsonText)
        {
            // Several sequences can fail at the same time: one prompt at a time (the others wait, and can be cancelled).
            using (context.EnterUserInteraction("JSON correction"))
            {
                var answer = context.Output.Ask(new UserPrompt
                {
                    Title = "AI JSON parsing error: fix the JSON and retry",
                    Message = errorMessage,
                    EditableText = jsonText ?? string.Empty,
                    Choices = new[] { "Retry with my changes", "Set empty ([])", "Cancel / Fail" }
                });
                switch (answer.Choice)
                {
                    case "Retry with my changes": return answer.Text ?? jsonText;
                    case "Set empty ([])": return "[]";
                    default: return null;
                }
            }
        }
    }
}
