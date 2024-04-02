using FunscriptToolbox.Core;
using FunscriptToolbox.SubtitlesVerbV2;
using System.Collections.Generic;
using System.Text;
using System;
using FunscriptToolbox.SubtitlesVerbsV2.Transcriptions;
using System.Linq;
using Newtonsoft.Json;

namespace FunscriptToolbox.SubtitlesVerbsV2.Outputs
{
    internal class SubtitleOutputWIPSrt : SubtitleOutput
    {
        public SubtitleOutputWIPSrt()
        {
            
        }

        public override bool NeedSubtitleForcedTimings => true;

        [JsonProperty(Order = 10)]
        public string FileSuffixe { get; set; }
        [JsonProperty(Order = 11)]
        public string[] TranscriptionOrder { get; set; }
        [JsonProperty(Order = 12)]
        public string[] TranslationOrder { get; set; }

        public override void CreateOutput(
            SubtitleGeneratorContext context,
            WorkInProgressSubtitles wipsub)
        {
            // TODO Add info/verbose logs
            // TODO cleanup/improve text in generated srt, especially % for text that overlap multiple forced timing.

            if (this.FileSuffixe == null)
            {
                throw new ArgumentNullException($"{typeof(SubtitleOutputWIPSrt).Name}.FileSuffixe");
            }

            var wipSubtitleFile = new SubtitleFile();

            var intersections = new List<TimeFrameIntersection>();
            var unmatchedTranscribedTexts = new List<TimeFrameIntersection>();

            var final = wipsub.SubtitlesForcedTiming?.ToArray()
                ?? wipsub.Transcriptions.First().Items.Select(
                    f => new SubtitleForcedTiming(f.StartTime, f.EndTime, SubtitleForcedTimingType.Voice, f.Text)).ToArray();

            var finalTranscriptionOrder = CreateFinalOrder(this.TranscriptionOrder, wipsub.Transcriptions.Select(f => f.Id));
            var finalTranslationOrder = CreateFinalOrder(this.TranslationOrder, wipsub.Transcriptions.SelectMany(f => f.Translations.Select(f2 => f2.Id)));

            foreach (var transcription in wipsub.Transcriptions
                .Where(f => finalTranscriptionOrder.Contains(f.Id))
                .OrderBy(t => Array.IndexOf(finalTranscriptionOrder, t.Id)))
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
                if (subtitleLocation.Type == SubtitleForcedTimingType.Screengrab)
                {
                    wipSubtitleFile.Subtitles.Add(
                        new Subtitle(
                            subtitleLocation.StartTime,
                            subtitleLocation.EndTime,
                            $"Screengrab: {subtitleLocation.Text}"));
                }
                else if (subtitleLocation.Type == SubtitleForcedTimingType.Context)
                {
                }
                else
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
                                .Where(f => finalTranslationOrder.Contains(f.Id))
                                .OrderBy(f => Array.IndexOf(finalTranslationOrder, f.Id)))
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
                            .Where(f => finalTranslationOrder.Contains(f.Id))
                            .OrderBy(f => Array.IndexOf(finalTranslationOrder, f.Id)))
                        {
                            builder.AppendLine($"   [{translation.Id}] {translation.Text}");
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
            var filename = context.BaseFilePath + this.FileSuffixe;
            context.SoftDelete(filename);
            wipSubtitleFile.SaveSrt(filename);
        }

        private string[] CreateFinalOrder(string[] order, IEnumerable<string> allIds)
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
