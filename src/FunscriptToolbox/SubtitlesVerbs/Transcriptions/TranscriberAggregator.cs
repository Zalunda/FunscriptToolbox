using FunscriptToolbox.Core;
using FunscriptToolbox.SubtitlesVerbs.AudioExtraction;
using FunscriptToolbox.SubtitlesVerbs.Translations;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    // This "transcriber" doesn't run speech-to-text.
    // It collates multiple transcriptions and their translations per timing
    // into aggregation lines, exposing them as a normal Transcription.
    public class TranscriberAggregator : Transcriber
    {
        public TranscriberAggregator()
        {
        }

        [JsonProperty(Order = 20)]
        public string TranscriptionIdToUseInsteadOfForcedTimings { get; set; }

        [JsonProperty(Order = 21)]
        public string[] TranscriptionsOrder { get; set; }

        [JsonProperty(Order = 22)]
        public string[] TranslationsOrder { get; set; }

        [JsonProperty(Order = 23)]
        public bool IncludeExtraTranscriptions { get; set; } = true;

        [JsonProperty(Order = 24)]
        public bool AutoSelectSingleChoice { get; set; } = false;

        [JsonProperty(Order = 25)]
        public string PartSeparator { get; set; } = string.Empty;

        public override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            IEnumerable<Transcriber> transcribers,
            out string reason)
        {
            // Validate the timings source:
            // - If an override transcription is specified, use it and validate it exists.
            // - Otherwise, require SubtitlesForcedTiming to be present (as before).
            if (this.TranscriptionIdToUseInsteadOfForcedTimings != null)
            {
                var timingTranscription = context.CurrentWipsub.Transcriptions
                    .FirstOrDefault(t => t.Id == this.TranscriptionIdToUseInsteadOfForcedTimings);

                if (timingTranscription == null)
                {
                    reason = $"Transcription '{this.TranscriptionIdToUseInsteadOfForcedTimings}' (timings override) not done yet.";
                    return false;
                }
            }
            else
            {
                if (context.CurrentWipsub.SubtitlesForcedTiming == null)
                {
                    reason = "SubtitlesForcedTiming not imported yet.";
                    return false;
                }
            }

            // Check if the all transcription/translation have been done
            foreach (var transcriber in transcribers?.Where(f => f.Enabled) ?? Array.Empty<Transcriber>())
            {
                var transcription = context.CurrentWipsub.Transcriptions
                    .FirstOrDefault(t => t.Id == transcriber.TranscriptionId);
                if (this.TranscriptionsOrder == null
                    || this.TranscriptionsOrder.Contains("*")
                    || this.TranscriptionsOrder.Contains(transcriber.TranscriptionId))
                {
                    if (transcription == null)
                    {
                        reason = $"Transcription '{transcriber.TranscriptionId}' not done yet.";
                        return false;
                    }

                    foreach (var translator in transcriber.Translators?.Where(f => f.Enabled) ?? Array.Empty<Translator>())
                    {
                        if (this.TranslationsOrder == null
                            || this.TranslationsOrder.Contains("*")
                            || this.TranslationsOrder.Contains(translator.TranslationId))
                        {
                            var translation = transcription.Translations.FirstOrDefault(
                                t => t.Id == translator.TranslationId);
                            if (!translator.IsFinished(transcription, translation))
                            {
                                reason = $"Translation '{transcriber.TranscriptionId}/{translator.TranslationId}' not done yet.";
                                return false;
                            }
                        }
                    }
                }
            }

            reason = null;
            return true;
        }

        public override Transcription Transcribe(
            SubtitleGeneratorContext context,
            PcmAudio pcmAudio,
            Language overrideLanguage)
        {
            var transcribedLanguage = overrideLanguage ?? this.Language;

            var subtitles = BuildSubtitlesAggregation(context).ToArray();

            // Optional verbose SRT for inspection
            if (context.IsVerbose)
            {
                var srt = new SubtitleFile();
                srt.Subtitles.AddRange(subtitles);
                srt.SaveSrt(context.GetPotentialVerboseFilePath($"{this.TranscriptionId}-aggregation.srt", DateTime.Now));
            }

            // Convert to TranscribedText items
            var items = subtitles
                .Select(s => new TranscribedText(s.StartTime, s.EndTime, s.Text))
                .ToArray();

            // No external cost; this is an internal collation step
            var costs = Array.Empty<TranscriptionCost>();

            return new Transcription(
                this.TranscriptionId,
                transcribedLanguage,
                items,
                costs);
        }

        private IEnumerable<Subtitle> BuildSubtitlesAggregation(SubtitleGeneratorContext context)
        {
            var finalForcedTimings = ((this.TranscriptionIdToUseInsteadOfForcedTimings != null)
                    ? context.CurrentWipsub.Transcriptions.FirstOrDefault(f => f.Id == this.TranscriptionIdToUseInsteadOfForcedTimings)
                    .Items.Select(t => new SubtitleForcedTiming(t.StartTime, t.EndTime, t.Text))
                    : context.CurrentWipsub.SubtitlesForcedTiming?.ToArray())
                ?? Array.Empty<SubtitleForcedTiming>();

            GetFinalOrders(context, out var finalTranscriptionsOrder, out var finalTranslationsOrder);

            var transcriptionsAnalysis = finalTranscriptionsOrder
                .Select(id => context.CurrentWipsub.Transcriptions.First(t => t.Id == id).GetAnalysis(context))
                .ToArray();

            var extraTranscriptions = IncludeExtraTranscriptions
                ? transcriptionsAnalysis
                    .SelectMany(ta => ta.ExtraTranscriptions.Select(tt => new ExtraTranscription(ta.Transcription.Id, tt)))
                    .OrderBy(item => item.TranscribedText.StartTime)
                    .ThenBy(item => Array.IndexOf(finalTranscriptionsOrder, item.TranscriptionId))
                    .ToList()
                : new List<ExtraTranscription>();

            var result = new List<Subtitle>();

            foreach (var forcedTiming in finalForcedTimings)
            {
                // Emit any "extra" transcriptions occurring before (or at) current timing
                result.AddRange(GetExtraSubtitles(extraTranscriptions, finalTranslationsOrder, forcedTiming.StartTime));

                string singleChoice = AutoSelectSingleChoice ? string.Empty : null;

                if (forcedTiming.ScreengrabText != null)
                {
                    result.Add(new Subtitle(forcedTiming.StartTime, forcedTiming.EndTime, $"{forcedTiming.ScreengrabText}"));
                }
                else if (forcedTiming.VoiceText != null)
                {
                    var builder = new StringBuilder();

                    foreach (var ta in transcriptionsAnalysis)
                    {
                        if (!ta.TimingsWithOverlapTranscribedTexts.TryGetValue(forcedTiming, out var overlaps) || overlaps.Length == 0)
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

                                var overlapInfo = string.Empty;
                                var text = overlap.TranscribedText.Text;

                                if (ta.TranscribedTextWithOverlapTimings.TryGetValue(overlap.TranscribedText, out var overlapOtherSide)
                                    && overlapOtherSide.Length > 1)
                                {
                                    var matchIndex = Array.FindIndex(overlapOtherSide, x => x.Timing == forcedTiming);
                                    if (matchIndex >= 0)
                                    {
                                        overlapInfo = $"[{matchIndex + 1}/{overlapOtherSide.Length}, {overlapOtherSide[matchIndex].WordsText}]";
                                    }
                                    text = string.Join(PartSeparator ?? string.Empty, overlapOtherSide.Select(o => o.WordsText));
                                }

                                builder.AppendLine($"[{ta.Transcription.Id}{number}] {text} {overlapInfo}");
                                AppendTranslationLines(builder, overlap.TranscribedText, finalTranslationsOrder);

                                var translations = overlap.TranscribedText
                                    .TranslatedTexts
                                    .Where(f => finalTranslationsOrder.Contains(f.Id))
                                    .ToArray();

                                // Only keep singleChoice if exactly one translation and no overlapInfo ambiguity
                                if (!string.IsNullOrEmpty(overlapInfo) || translations.Length != 1)
                                {
                                    singleChoice = null;
                                }
                                else
                                {
                                    if (singleChoice == string.Empty)
                                    {
                                        singleChoice = translations.First().Text;
                                    }
                                    else
                                    {
                                        // Multiple distinct candidates => cancel singleChoice
                                        singleChoice = null;
                                    }
                                }
                            }
                        }
                    }

                    result.Add(new Subtitle(
                        forcedTiming.StartTime,
                        forcedTiming.EndTime,
                        singleChoice ?? builder.ToString()));
                }
            }

            // Flush any remaining extras after the last forced timing
            result.AddRange(GetExtraSubtitles(extraTranscriptions, finalTranslationsOrder, currentTiming: null));

            return result;
        }

        private void GetFinalOrders(SubtitleGeneratorContext context, out string[] finalTranscriptionsOrder, out string[] finalTranslationsOrder)
        {
            finalTranscriptionsOrder = CreateFinalOrder(
                TranscriptionsOrder ?? Array.Empty<string>(),
                context.CurrentWipsub.Transcriptions.Select(f => f.Id));
            finalTranslationsOrder = CreateFinalOrder(
                TranslationsOrder ?? Array.Empty<string>(),
                context.CurrentWipsub.Transcriptions.SelectMany(f => f.Translations.Select(f2 => f2.Id)));
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

        private sealed class ExtraTranscription
        {
            public string TranscriptionId { get; }
            public TranscribedText TranscribedText { get; }

            public ExtraTranscription(string transcriptionId, TranscribedText transcribedText)
            {
                TranscriptionId = transcriptionId;
                TranscribedText = transcribedText;
            }
        }

        private static IEnumerable<Subtitle> GetExtraSubtitles(
            List<ExtraTranscription> extraTranscriptions,
            string[] finalTranslationsOrder,
            TimeSpan? currentTiming)
        {
            while (extraTranscriptions.Count > 0 &&
                   (currentTiming == null || extraTranscriptions.First().TranscribedText.StartTime <= currentTiming))
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