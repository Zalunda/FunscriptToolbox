﻿using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.Transcriptions;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerbs.Translations
{
    internal class TranslatorDeepLWithFiles : Translator
    {
        public const string ValidationStringToKnowIfFileTranslated = "THIS  LINE  IS  HERE  TO  DETECT  IF  FILE  HAS  BEEN  TRANSLATED";

        public TranslatorDeepLWithFiles()
        {
        }

        public int MaximumCharactersPerFile { get; set; } = 1300;

        public override void Translate(
            SubtitleGeneratorContext context,
            Transcription transcription,
            Translation translation)
        {
            var allTexts = transcription.Items.Select(item => item.Text).ToArray();

            foreach (var fullpath in Directory.GetFiles(
                PathExtension.SafeGetDirectoryName(context.CurrentBaseFilePath),
                "*.*"))
            {
                var filename = Path.GetFileName(fullpath);
                var match = Regex.Match(
                    filename, 
                    Regex.Escape($"TODO-{transcription.Id}-{translation.Id}-") + $"(?<startIndex>\\d+)-(?<nbItems>\\d+).txt", 
                    RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    var startIndex = int.Parse(match.Groups["startIndex"].Value) - 1;
                    var nbItems = int.Parse(match.Groups["nbItems"].Value);
                    var lines = File.ReadAllLines(fullpath);
                    context.SoftDelete(fullpath);
                    context.WriteInfo($"        Analysing existing file '{filename}'...");
                    var nbTranslationsAdded = 0;
                    if (lines.Length < nbItems)
                    {
                        context.WriteInfo($"        Finished, missing lines in file ({lines.Length} < {nbItems}), ignoring:");
                    }
                    else if (lines.Any(line => line.Contains(ValidationStringToKnowIfFileTranslated)))
                    {
                        context.WriteInfo($"        Finished, special line still present as is, ignoring:");
                    }
                    else
                    {
                        for (int lineIndex = 0; lineIndex < nbItems; lineIndex++) 
                        {
                            var transcriptionItem = transcription.Items[startIndex + lineIndex];
                            var originalText = transcriptionItem.Text;
                            var translatedText = lines[lineIndex];
                            var oldTranslatedItem = transcriptionItem.TranslatedTexts.FirstOrDefault(tt => tt.Id == translation.Id);
                            if (oldTranslatedItem == null || (oldTranslatedItem.Text == originalText))
                            {
                                transcriptionItem.TranslatedTexts.Remove(oldTranslatedItem);
                                transcriptionItem.TranslatedTexts.Add(new TranslatedText(translation.Id, translatedText));
                                nbTranslationsAdded++;
                            }
                        }
                        context.WriteInfo($"        Finished:");
                    }
                    context.WriteInfo($"            Nb translations added: {nbTranslationsAdded}");
                }
            }

            if (!this.IsFinished(transcription, translation))
            {
                var fileContent = new StringBuilder();
                var index = 0;
                var startIndexInFile = index;
                do
                {
                    var currentItem = transcription.Items.Skip(index).FirstOrDefault();

                    if (currentItem != null 
                        && fileContent.Length + currentItem.Text.Length + ValidationStringToKnowIfFileTranslated.Length < this.MaximumCharactersPerFile)
                    {
                        fileContent.AppendLine(currentItem.Text);
                    }
                    else
                    {
                        var nbItems = index - startIndexInFile;
                        var filepath = $"{context.CurrentBaseFilePath}.TODO-{transcription.Id}-{translation.Id}-{startIndexInFile + 1:D04}-{nbItems:D04}.txt";
                        context.WriteInfo($"        Creating file '{Path.GetFileName(filepath)}' (contains {nbItems} texts)...");
                        fileContent.AppendLine(ValidationStringToKnowIfFileTranslated);
                        context.SoftDelete(filepath);
                        File.WriteAllText(filepath, fileContent.ToString());

                        fileContent.Clear();
                        startIndexInFile = index;
                        if (currentItem != null)
                        {
                            fileContent.AppendLine(currentItem.Text);
                        }

                        context.AddUserTodo($"Give the content of the file '{Path.GetFileName(filepath)}' to an DeepL and then put its response in the same file.");
                    }
                } while (++index <= allTexts.Length);
            }
        }
    }
}