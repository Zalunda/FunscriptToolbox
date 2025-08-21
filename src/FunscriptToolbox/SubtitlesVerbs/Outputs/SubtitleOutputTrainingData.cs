using FunscriptToolbox.Core;
using Newtonsoft.Json;
using System;
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
        public string SrtSuffix { get; set; }
        [JsonProperty(Order = 12, Required = Required.Always)]
        public string TranscriptionId { get; set; }

        public override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            var srtFullpath = context.CurrentBaseFilePath + this.SrtSuffix;
            if (!context.CurrentWipsub.Transcriptions.Any(f => f.Id == this.TranscriptionId && f.IsFinished))
            {
                reason = $"Transcription '{TranscriptionId}' not done yet.";
                return false;
            }
            else if (!File.Exists(srtFullpath))
            {
                reason = $"File '{Path.GetFileName(srtFullpath)}' does not exists.";
                return false;
            }
            else
            {
                reason = null;
                return true;
            }
        }

        private class SimpleSubtitle : ITiming
        {
            public SimpleSubtitle(TimeSpan startTime, TimeSpan endTime, string text)
            {
                StartTime = startTime;
                EndTime = endTime;
                Text = text;
            }

            public TimeSpan StartTime { get; }
            public TimeSpan EndTime { get; }
            public string Text { get; }
        }

        public override void CreateOutput(
            SubtitleGeneratorContext context)
        {
            var forcedTimings = context.CurrentWipsub.SubtitlesForcedTiming;

            var srtFullpath = context.CurrentBaseFilePath + this.SrtSuffix;
            var finalSubtitles = SubtitleFile.FromSrtFile(srtFullpath)
                .Subtitles
                .Select(sub => new SimpleSubtitle(sub.StartTime, sub.EndTime, sub.Text))
                .ToArray();
            var mergedVadTranscription = context.CurrentWipsub.Transcriptions.First(f => f.Id == this.TranscriptionId);

            var analysis = mergedVadTranscription.GetAnalysis(finalSubtitles);
            string lastContext = null;
            var result = new List<dynamic>();
            foreach (var finalItem in finalSubtitles)
            {
                var currentContext = forcedTimings?.GetContextAt(finalItem.StartTime);
                var currentTalker = forcedTimings?.GetTalkerAt(finalItem.StartTime, finalItem.EndTime);
                if (analysis.TimingsWithOverlapTranscribedTexts.TryGetValue(finalItem, out var mergedOverlap))
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
                            finalItem.StartTime,
                            Context = currentContext == lastContext ? null : currentContext,
                            Talker = currentTalker,
                            Original = mergedOverlap.First(x => x.Timing == finalItem).WordsText,
                            OriginalIsPartOfSingleTranscription = importM.Select(f => f.WordsText).ToArray(),
                            Translation = finalItem.Text,
                        });
                    }
                    else
                    {
                        result.Add(new
                        {
                            Context = currentContext == lastContext ? null : currentContext,
                            Talker = currentTalker,
                            finalItem.StartTime,
                            Original = originalText,
                            Translation = finalItem.Text,
                        });
                    }
                    lastContext = currentContext;
                }
                else
                {
                    result.Add(new
                    {
                        finalItem.StartTime,
                        Original = "** tool was not able to transcribe that part **",
                        Translation = finalItem.Text,
                    });
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
