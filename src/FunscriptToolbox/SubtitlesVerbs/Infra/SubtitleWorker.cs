using FunscriptToolbox.Core;
using FunscriptToolbox.SubtitlesVerbs.Transcriptions;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public abstract class SubtitleWorker
    {
        protected static void SaveDebugSrtIfVerbose(SubtitleGeneratorContext context, Transcription transcription)
        {
            if (context.IsVerbose)
            {
                var srt = new SubtitleFile();
                srt.Subtitles.AddRange(transcription.Items.Select(item =>
                    new Subtitle(
                        item.StartTime,
                        item.EndTime,
                        string.Join("\n", item.Metadata.Select(kvp => $"{{{kvp.Key}:{kvp.Value}}}")))));
                srt.SaveSrt(context.GetPotentialVerboseFilePath($"{transcription.Id}.srt", DateTime.Now));
            }
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