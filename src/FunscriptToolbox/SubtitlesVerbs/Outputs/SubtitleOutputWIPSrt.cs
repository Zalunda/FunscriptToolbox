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

        [JsonProperty(Order = 10, Required = Required.Always)]
        public string FileSuffix { get; set; }
        [JsonProperty(Order = 11, Required = Required.Always)]
        public string[] TranscriptionsOrder { get; set; }
        [JsonProperty(Order = 12, Required = Required.Always)]
        public string[] TranslationsOrder { get; set; }

        public override void CreateOutput(
            SubtitleGeneratorContext context,
            WorkInProgressSubtitles wipsub)
        {
            // TODO cleanup/improve text in generated srt, especially % for text that overlap multiple forced timing.

            if (this.FileSuffix == null)
            {
                throw new ArgumentNullException($"{typeof(SubtitleOutputWIPSrt).Name}.FileSuffix");
            }

            var wipSubtitleFile = new SubtitleFile();

            var intersections = new List<TimeFrameIntersection>();
            var unmatchedTranscribedTexts = new List<TimeFrameIntersection>();

            // TODO Fix bug if transcriptions is empty
            var final = wipsub.SubtitlesForcedTiming?.ToArray()
                ?? wipsub.Transcriptions.FirstOrDefault()?.Items.Select(
                    f => new SubtitleForcedTiming(f.StartTime, f.EndTime, f.Text)).ToArray();

            var finalTranscriptionsOrder = CreateFinalOrder(this.TranscriptionsOrder, wipsub.Transcriptions.Select(f => f.Id));
            var finalTranslationsOrder = CreateFinalOrder(this.TranslationsOrder, wipsub.Transcriptions.SelectMany(f => f.Translations.Select(f2 => f2.Id)));

            foreach (var transcription in wipsub.Transcriptions
                .Where(f => finalTranscriptionsOrder.Contains(f.Id))
                .OrderBy(t => Array.IndexOf(finalTranscriptionsOrder, t.Id)))
            {
                foreach (var tt in transcription.Items)
                {
                    var matches = final
                        .Select(location => TimeFrameIntersection.From(transcription.Id, location, tt))
                        .Where(f => f != null)
                        .ToArray();
                    if (matches.Length == 0)
                    {
                        unmatchedTranscribedTexts.Add(
                            new TimeFrameIntersection(
                                transcription.Id,
                                tt.StartTime,
                                tt.EndTime,
                                null,
                                tt));
                    }
                    else
                    {
                        var totalTimeMatched = matches.Select(m => m.Duration.TotalMilliseconds).Sum();

                        var index = 1;
                        foreach (var match in matches)
                        {
                            match.Index = index++;
                            match.Number = matches.Length;
                            match.PercentageTime = (int)(100 * match.Duration.TotalMilliseconds / totalTimeMatched);
                            intersections.Add(match);
                        }
                    }
                }
            }

            TimeSpan? previousEnd = null;
            foreach (var subtitleLocation in final)
            {
                if (subtitleLocation.ScreengrabText != null)
                {
                    wipSubtitleFile.Subtitles.Add(
                        new Subtitle(
                            subtitleLocation.StartTime,
                            subtitleLocation.EndTime,
                            $"Screengrab: {subtitleLocation.ScreengrabText}"));
                }
                else if (subtitleLocation.VoiceText != null)
                {
                    if (previousEnd != null)
                    {
                        foreach (var utt in unmatchedTranscribedTexts
                            .Where(f => f.StartTime < subtitleLocation.StartTime)
                            .ToArray())
                        {
                            var builder2 = new StringBuilder();

                            builder2.AppendLine("** OUT OF SCOPE **");
                            builder2.AppendLine($"[{utt.TranscriptionId}] {utt.TranscribedText.Text}");
                            foreach (var translation in utt
                                .TranscribedText
                                .TranslatedTexts
                                .Where(f => finalTranslationsOrder.Contains(f.Id))
                                .OrderBy(f => Array.IndexOf(finalTranslationsOrder, f.Id)))
                            {
                                builder2.AppendLine($"   [{translation.Id}] {translation.Text}");
                            }

                            wipSubtitleFile.Subtitles.Add(
                                new Subtitle(
                                    utt.StartTime,
                                    utt.EndTime,
                                    builder2.ToString()));

                            unmatchedTranscribedTexts.Remove(utt);
                        }
                    }

                    var builder = new StringBuilder();
                    if (finalTranscriptionsOrder.Length == 1 && finalTranslationsOrder.Length == 1)
                    {
                        foreach (var item in intersections.Where(f => f.Location == subtitleLocation))
                        {
                            foreach (var translation in item
                                .TranscribedText
                                .TranslatedTexts
                                .Where(f => finalTranslationsOrder.Contains(f.Id))
                                .OrderBy(f => Array.IndexOf(finalTranslationsOrder, f.Id)))
                            {
                                builder.AppendLine($"{translation.Text}");
                            }
                        }
                    }
                    else
                    {
                        foreach (var item in intersections.Where(f => f.Location == subtitleLocation))
                        {
                            if (builder.Length > 0)
                            {
                                builder.AppendLine("--------------");
                            }

                            var xyz = item.Number > 1 ? $"{item.Index}/{item.Number}," : string.Empty;
                            builder.AppendLine($"[{item.TranscriptionId}] {item.TranscribedText.Text} ({xyz}{item.PercentageTime}%, {item.MatchPercentage}%)");
                            foreach (var translation in item
                                .TranscribedText
                                .TranslatedTexts
                                .Where(f => finalTranslationsOrder.Contains(f.Id))
                                .OrderBy(f => Array.IndexOf(finalTranslationsOrder, f.Id)))
                            {
                                builder.AppendLine($"   [{translation.Id}] {translation.Text}");
                            }
                        }
                    }

                    wipSubtitleFile.Subtitles.Add(
                        new Subtitle(
                            subtitleLocation.StartTime,
                            subtitleLocation.EndTime,
                            builder.ToString()));
                    previousEnd = subtitleLocation.EndTime;
                }
            }

            wipSubtitleFile.ExpandTiming(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.5));
            var filename = context.CurrentBaseFilePath + this.FileSuffix;
            context.SoftDelete(filename);
            wipSubtitleFile.SaveSrt(filename);
        }

        private class TimeFrameIntersection
        {
            internal int Number;

            public static TimeFrameIntersection From(
                string transcriptionId,
                SubtitleForcedTiming location,
                TranscribedText tt)
            {
                var intersectionStartTime = location.StartTime > tt.StartTime ? location.StartTime : tt.StartTime;
                var intersectionEndTime = location.EndTime < tt.EndTime ? location.EndTime : tt.EndTime;
                return (intersectionStartTime < intersectionEndTime)
                    ? new TimeFrameIntersection(
                        transcriptionId,
                        intersectionStartTime,
                        intersectionEndTime,
                        location,
                        tt)
                    : null;
            }

            public TimeFrameIntersection(
                string transcriptionId,
                TimeSpan startTime,
                TimeSpan endTime,
                SubtitleForcedTiming location,
                TranscribedText tt)
            {
                TranscriptionId = transcriptionId;
                StartTime = startTime;
                EndTime = endTime;
                Location = location;
                TranscribedText = tt;
                if (location != null)
                {
                    MatchPercentage = (int)(100 * (Math.Min(endTime.TotalMilliseconds, location.EndTime.TotalMilliseconds)
                        - Math.Max(startTime.TotalMilliseconds, location.StartTime.TotalMilliseconds)) / location.Duration.TotalMilliseconds);
                }
            }

            public string TranscriptionId { get; }
            public TimeSpan StartTime { get; }
            public TimeSpan EndTime { get; }
            public TimeSpan Duration => EndTime - StartTime;
            public SubtitleForcedTiming Location { get; }
            public TranscribedText TranscribedText { get; }
            public int MatchPercentage { get; }
            public int Index { get; internal set; }
            public int PercentageTime { get; internal set; }
        }
    }
}
