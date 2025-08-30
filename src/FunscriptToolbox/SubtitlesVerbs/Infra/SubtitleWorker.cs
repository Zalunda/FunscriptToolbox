using FunscriptToolbox.Core;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public abstract class SubtitleWorker
    {
        public abstract void Execute(SubtitleGeneratorContext context);

        protected static void SaveDebugSrtIfVerbose(SubtitleGeneratorContext context, TimedItemWithMetadataCollection container)
        {
            if (context.IsVerbose)
            {
                var srt = new SubtitleFile();
                srt.Subtitles.AddRange(container.GetItems().Select(item =>
                    new Subtitle(
                        item.StartTime,
                        item.EndTime,                        
                        string.Join("\n", item.Metadata.Select(kvp => $"{{{kvp.Key}:{AddNewLineIfMultilines(kvp.Value)}}}")))));
                srt.SaveSrt(context.GetPotentialVerboseFilePath($"{container.Id}.srt", DateTime.Now));
            }
        }

        private static string AddNewLineIfMultilines(string value)
        {
            return value == null
                ? null
                : (value.Contains("\n")) 
                    ? "\n" + value
                    : value;
        }
        protected string[] CreateFinalOrder(string[] order, IEnumerable<string> allIds)
        {
            if (order == null)
                return allIds.Distinct().ToArray();

            var remainingCandidats = allIds.Distinct().ToList();
            var finalOrder = new List<string>();
            foreach (var id in order)
            {
                if (id == "*")
                {
                    finalOrder.AddRange(remainingCandidats);
                    break;
                }
                else if (remainingCandidats.Contains(id))
                {
                    finalOrder.Add(id);
                    remainingCandidats.Remove(id);
                }
            }

            return finalOrder.ToArray();
        }
    }
}