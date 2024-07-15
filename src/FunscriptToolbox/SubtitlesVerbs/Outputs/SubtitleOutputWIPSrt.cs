using FunscriptToolbox.Core;
using FunscriptToolbox.SubtitlesVerbs.Transcriptions;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbs.Outputs
{
    internal class SubtitleOutputWIPSrt : SubtitleOutput
    {
        public SubtitleOutputWIPSrt()
        {
            
        }

        public override bool NeedSubtitleForcedTimings => true;

        public override string Description => $"{base.Description}: {this.FileSuffix}";

        [JsonProperty(Order = 10, Required = Required.Always)]
        public string FileSuffix { get; set; }
        [JsonProperty(Order = 11, Required = Required.Always)]
        public string[] TranscriptionsOrder { get; set; }
        [JsonProperty(Order = 12, Required = Required.Always)]
        public string[] TranslationsOrder { get; set; }

        [JsonProperty(Order = 13)]
        public bool IncludeExtraTranscriptions { get; set; } = true;

        [JsonProperty(Order = 14)]
        public int MinimumOverlapPercentage { get; set; } = 10;
        [JsonProperty(Order = 15)]
        public TimeSpan MinimumSubtitleDuration { get; set; } = TimeSpan.FromSeconds(1.5);
        [JsonProperty(Order = 16)]
        public TimeSpan ExpandSubtileDuration { get; set; } = TimeSpan.FromSeconds(0.5);

        [JsonProperty(Order = 20)]
        public SubtitleToInject[] SubtitlesToInject { get; set; }

        public override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            if (context.CurrentWipsub.Transcriptions.Count == 0)
            {
                reason = "No transcription available.";
                return false;
            }
            else if (context.CurrentWipsub.SubtitlesForcedTiming == null)
            {
                reason = "SubtitlesForcedTiming not imported yet.";
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
            var finalForcedTimings = context.CurrentWipsub.SubtitlesForcedTiming?.ToArray()
                ?? context.CurrentWipsub.Transcriptions.FirstOrDefault()?.Items.Select(
                    f => new SubtitleForcedTiming(f.StartTime, f.EndTime, f.Text)).ToArray();

            var finalTranscriptionsOrder = CreateFinalOrder(this.TranscriptionsOrder, context.CurrentWipsub.Transcriptions.Select(f => f.Id));
            var finalTranslationsOrder = CreateFinalOrder(this.TranslationsOrder, context.CurrentWipsub.Transcriptions.SelectMany(f => f.Translations.Select(f2 => f2.Id)));

            var transcriptionsAnalysis = finalTranscriptionsOrder
                .Select(id => context.CurrentWipsub.Transcriptions.First(t => t.Id == id).GetAnalysis(context, MinimumOverlapPercentage))
                .ToArray();
            var extraTranscriptions = IncludeExtraTranscriptions 
                ? transcriptionsAnalysis
                    .SelectMany(ta => ta.ExtraTranscriptions.Select(tt => new ExtraTranscription(ta.Transcription.Id, tt)))
                    .OrderBy(item => item.TranscribedText.StartTime)
                    .ThenBy(item => Array.IndexOf(finalTranscriptionsOrder, item.TranscriptionId))
                    .ToList()
                : new List<ExtraTranscription>();

            var wipSubtitleFile = new SubtitleFile();

            TimeSpan? previousEnd = null;
            foreach (var forcedTiming in finalForcedTimings)
            {
                wipSubtitleFile.Subtitles.AddRange(GetExtraSubtitles(extraTranscriptions, finalTranslationsOrder, forcedTiming.StartTime));

                if (forcedTiming.ScreengrabText != null)
                {
                    wipSubtitleFile.Subtitles.Add(
                        new Subtitle(
                            forcedTiming.StartTime,
                            forcedTiming.EndTime,
                            $"Screengrab: {forcedTiming.ScreengrabText}"));
                }
                else if (forcedTiming.VoiceText != null)
                {
                    var builder = new StringBuilder();
                    foreach (var ta in transcriptionsAnalysis)
                    {
                        if (!ta.ForcedTimingsWithOverlapTranscribedTexts.TryGetValue(forcedTiming, out var overlaps) || overlaps.Length == 0)
                        {
                            builder.AppendLine($"[{ta.Transcription.Id}] ** NO TRANSCRIPTION FOUND **");
                        }
                        else
                        {
                            var index = 1;
                            foreach (var overlap in overlaps)
                            {
                                var number = (overlaps.Length > 1)
                                    ? $",{index++}/{overlaps.Length}"
                                    : string.Empty;
                                builder.AppendLine($"[{ta.Transcription.Id}{number}] {overlap.TranscribedText.Text} {overlap.OverlapInfo}");
                                AppendTranslationLines(builder, overlap.TranscribedText, finalTranslationsOrder);
                            }
                        }
                    }    

                    wipSubtitleFile.Subtitles.Add(
                        new Subtitle(
                            forcedTiming.StartTime,
                            forcedTiming.EndTime,
                            builder.ToString()));
                    previousEnd = forcedTiming.EndTime;
                }
            }

            wipSubtitleFile.ExpandTiming(this.MinimumSubtitleDuration, this.ExpandSubtileDuration);
            wipSubtitleFile.Subtitles.AddRange(
                GetAdjustedSubtitlesToInject(wipSubtitleFile.Subtitles, this.SubtitlesToInject, context.CurrentWipsub.PcmAudio.Duration));
            wipSubtitleFile.Subtitles.AddRange(
                GetExtraSubtitles(extraTranscriptions, finalTranslationsOrder));

            var filename = context.CurrentBaseFilePath + this.FileSuffix;
            context.SoftDelete(filename);
            wipSubtitleFile.SaveSrt(filename);
        }

        private static void AppendTranslationLines(
            StringBuilder builder, 
            TranscribedText transcribedText, 
            string[] finalTranslationsOrder)
        {
            foreach (var translation in transcribedText
                .TranslatedTexts
                .Where(f => finalTranslationsOrder.Contains(f.Id))
                .OrderBy(f => Array.IndexOf(finalTranslationsOrder, f.Id)))
            {
                builder.AppendLine($"   [{translation.Id}] {translation.Text}");
            }
        }

        internal class ExtraTranscription
        {
            public string TranscriptionId { get; }
            public TranscribedText TranscribedText { get; }

            public ExtraTranscription(string transcriptionId, TranscribedText transcribedText)
            {
                TranscriptionId = transcriptionId;
                TranscribedText = transcribedText;
            }
        }


        private IEnumerable<Subtitle> GetExtraSubtitles(
            List<ExtraTranscription> extraTranscriptions,
            string[] finalTranslationsOrder,
            TimeSpan? currentTiming = null)
        {
            while (extraTranscriptions.Count > 0 && (currentTiming == null || extraTranscriptions.First().TranscribedText.StartTime <= currentTiming))
            {
                var extra = extraTranscriptions.First();
                extraTranscriptions.RemoveAt(0);

                var builder = new StringBuilder();

                builder.AppendLine("** EXTRA TRANSCRIPTION **");
                builder.AppendLine($"[{extra.TranscriptionId}] {extra.TranscribedText.Text}");
                AppendTranslationLines(builder, extra.TranscribedText, finalTranslationsOrder);

                yield return new Subtitle(
                        extra.TranscribedText.StartTime,
                        extra.TranscribedText.EndTime,
                        builder.ToString());
            }
        }
    }
}
