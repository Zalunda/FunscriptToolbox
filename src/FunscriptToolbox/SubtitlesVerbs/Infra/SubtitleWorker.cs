using FunscriptToolbox.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public abstract class SubtitleWorker
    {
        public abstract void Execute(SubtitleGeneratorContext context);

        protected static void SaveDebugSrtIfVerbose(SubtitleGeneratorContext context, TimedItemWithMetadataCollection container)
        {
            if (context.IsVerbose)
            {
                CreateMetadatasSrt(
                    context.GetPotentialVerboseFilePath($"{container.Id}.srt", DateTime.Now),
                    container);
            }
        }

        protected static void CreateMetadatasSrt(string filename, TimedItemWithMetadataCollection container)
        {
            var srt = new SubtitleFile();
            srt.Subtitles.AddRange(container.GetItems().Select(item =>
                new Subtitle(
                    item.StartTime,
                    item.EndTime,
                    string.Join("\n", item.Metadata.Select(kvp => $"{{{kvp.Key}:{AddNewLineIfMultilines(kvp.Value)}}}")))));
            srt.SaveSrt(filename);
        }

        protected static IEnumerable<TimedItemWithMetadata> ReadMetadataFromSrt(string fullpath)
        {
            const string MetadataExtractionRegex = @"{(?<name>[^}:]*)(\:(?<value>[^}]*))?}";

            var subtitleFile = SubtitleFile.FromSrtFile(fullpath);
            return subtitleFile
                .Subtitles
                .Select(subtitle => new TimedItemWithMetadata(
                    subtitle.StartTime,
                    subtitle.EndTime,
                    metadata: new MetadataCollection(
                        Regex
                        .Matches(subtitle.Text, MetadataExtractionRegex)
                        .Cast<Match>()
                        .ToDictionary(
                            match => match.Groups["name"].Value,
                            match => match.Groups["value"].Success ? match.Groups["value"].Value : string.Empty))));
        }

        protected static string AddNewLineIfMultilines(string value)
        {
            return value == null
                ? null
                : (value.Contains("\n")) 
                    ? "\n" + value
                    : value;
        }
    }
}