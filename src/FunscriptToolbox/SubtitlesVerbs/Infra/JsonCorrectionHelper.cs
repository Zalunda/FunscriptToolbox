using FunscriptToolbox.SubtitlesVerbs.Progress;
using System;
using System.Diagnostics;
using System.IO;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public static class JsonCorrectionHelper
    {
        /// <summary>
        /// Prompts the user to manually correct invalid JSON.
        /// On Windows: launches an interactive WPF correction window with Retry, Set Empty ([]), and Cancel.
        /// On Linux/macOS: writes the invalid JSON with error details to a temporary file and opens the default text editor.
        /// </summary>
        public static string PromptForFix(SubtitleGeneratorContext context, string errorMessage, string jsonText)
        {
            if (context.Output.IsWebUi)
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

            // (also in the console: the task is 'waiting for the user', so the progress line is not drawn over the prompt)
            using (context.EnterUserInteraction("JSON correction"))
            {
                return PromptForFix(errorMessage, jsonText);
            }
        }

        private static string PromptForFix(string errorMessage, string jsonText)
        {
#if WINDOWS
            if (OperatingSystem.IsWindows())
            {
                return FunscriptToolbox.UI.Test.PromptForJsonFix(errorMessage, jsonText);
            }
#endif
            return PromptForFixWithEditor(errorMessage, jsonText);
        }

        private static string PromptForFixWithEditor(string errorMessage, string jsonText)
        {
            var tempFile = Path.Combine(Path.GetTempPath(), $"fstb-json-fix-{Guid.NewGuid():N}.json");

            try
            {
                File.WriteAllText(tempFile, jsonText ?? string.Empty);

                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("================ AI JSON Parsing Error ================");
                Console.WriteLine(errorMessage);
                Console.WriteLine("========================================================");
                Console.ResetColor();
                Console.WriteLine($"Temporary file created for editing: {tempFile}");
                Console.WriteLine();

                // Open the default system text editor
                TryOpenEditor(tempFile);

                while (true)
                {
                    Console.WriteLine("Options:");
                    Console.WriteLine("  [r] Retry parsing (after saving changes in your editor)");
                    Console.WriteLine("  [e] Set Empty '[]' (continue as if AI response was empty)");
                    Console.WriteLine("  [c] Cancel / Fail");
                    Console.Write("Enter choice [r/e/c]: ");

                    var input = Console.ReadLine()?.Trim().ToLowerInvariant();
                    if (input == "r" || input == "retry")
                    {
                        if (File.Exists(tempFile))
                        {
                            return File.ReadAllText(tempFile);
                        }
                        return jsonText;
                    }
                    else if (input == "e" || input == "empty")
                    {
                        return "[]";
                    }
                    else if (input == "c" || input == "cancel")
                    {
                        return null;
                    }
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(tempFile))
                    {
                        File.Delete(tempFile);
                    }
                }
                catch
                {
                    // Ignore temp file cleanup errors
                }
            }
        }

        private static void TryOpenEditor(string filePath)
        {
            try
            {
                if (OperatingSystem.IsMacOS())
                {
                    Process.Start("open", $"\"{filePath}\"");
                }
                else if (OperatingSystem.IsLinux())
                {
                    var visual = Environment.GetEnvironmentVariable("VISUAL") ?? Environment.GetEnvironmentVariable("EDITOR");
                    if (!string.IsNullOrEmpty(visual))
                    {
                        Process.Start(visual, $"\"{filePath}\"");
                    }
                    else
                    {
                        Process.Start("xdg-open", $"\"{filePath}\"");
                    }
                }
                else
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = filePath,
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not automatically open editor ({ex.Message}). Please edit the file manually.");
            }
        }
    }
}
