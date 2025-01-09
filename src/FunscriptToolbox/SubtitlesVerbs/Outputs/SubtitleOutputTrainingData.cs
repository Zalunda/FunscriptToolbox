using Newtonsoft.Json;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Outputs
{
    internal class SubtitleOutputTrainingData : SubtitleOutput
    {
        public SubtitleOutputTrainingData()
        {
            
        }

        public override bool NeedSubtitleForcedTimings => true;

        public override string Description => $"{base.Description}: {this.FileSuffix}";

        [JsonProperty(Order = 10, Required = Required.Always)]
        public string FileSuffix { get; set; }
        [JsonProperty(Order = 11, Required = Required.Always)]
        public string ImportId { get; set; }
        [JsonProperty(Order = 12, Required = Required.Always)]
        public string MergedVadId { get; set; }

        public override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            if (context.CurrentWipsub.SubtitlesForcedTiming == null)
            {
                reason = "SubtitlesForcedTiming not imported yet.";
                return false;
            }
            else if (!context.CurrentWipsub.Transcriptions.Any(f => f.Id == this.ImportId && f.Items.Length > 0))
            {
                reason = $"Transcription '{ImportId}' not done yet.";
                return false;
            }
            else if (!context.CurrentWipsub.Transcriptions.Any(f => f.Id == this.MergedVadId && f.Items.Length > 0))
            {
                reason = $"Transcription '{MergedVadId}' not done yet.";
                return false;
            }
            else
            {
                reason = null;
                return true;
            }
        }

        public override void CreateOutput(
            SubtitleGeneratorContext context)
        {
            var forcedTimings = context.CurrentWipsub.SubtitlesForcedTiming;

            var importTranscription = context.CurrentWipsub.Transcriptions.First(f => f.Id == this.ImportId);
            var mergedVadTranscription = context.CurrentWipsub.Transcriptions.First(f => f.Id == this.MergedVadId);

            var analysis = mergedVadTranscription.GetAnalysis(importTranscription.Items);
            string lastContext = null;
            var result = new List<dynamic>();
            foreach (var importItem in importTranscription.Items)
            {
                var currentContext = forcedTimings.GetContextAt(importItem.StartTime);
                var currentTalker = forcedTimings.GetTalkerAt(importItem.StartTime, importItem.EndTime);
                if (analysis.TimingsWithOverlapTranscribedTexts.TryGetValue(importItem, out var mergedOverlap))
                {
                    var originalText = string.Join("\n", mergedOverlap.Select(t => t.TranscribedText.Text));
                    var importM = mergedOverlap
                        .SelectMany(mo => analysis.TranscribedTextWithOverlapTimings[mo.TranscribedText])
                        .Distinct()
                        .ToArray();
                    if (mergedOverlap.Length == 1 && importM.Length > 1)
                    {
                        result.Add(new
                        {
                            importItem.StartTime,
                            Context = currentContext == lastContext ? null : currentContext,
                            Talker = currentTalker,
                            Original = mergedOverlap.First(x => x.Timing == importItem).WordsText,
                            OriginalIsPartOfSingleTranscription = importM.Select(f => f.WordsText).ToArray(),
                            Translation = importItem.Text,
                        });
                    }
                    else
                    {
                        result.Add(new
                        {
                            Context = currentContext == lastContext ? null : currentContext,
                            Talker = currentTalker,
                            importItem.StartTime,
                            Original = originalText,
                            Translation = importItem.Text,
                        });
                    }
                    lastContext = currentContext;
                }
                else
                {
                    // Ignore
                }
            }

            var filename = context.CurrentBaseFilePath + this.FileSuffix;
            context.SoftDelete(filename);
            File.WriteAllText(
                filename,
                JsonConvert.SerializeObject(
                    result,
                    Formatting.Indented,
                    new JsonSerializerSettings
                    {
                        NullValueHandling = NullValueHandling.Ignore
                    }));
        }
    }
}
