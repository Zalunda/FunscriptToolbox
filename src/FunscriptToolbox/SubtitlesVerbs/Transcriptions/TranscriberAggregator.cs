//using FunscriptToolbox.Core;
//using FunscriptToolbox.SubtitlesVerbs.Infra;
//using Newtonsoft.Json;
//using System;
//using System.Collections.Generic;
//using System.Linq;
//using System.Text;

//namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
//{
//    // This "transcriber" doesn't run speech-to-text.
//    // It collates multiple transcriptions and their translations per timing
//    // into aggregation lines, exposing them as a normal Transcription.
//    public class TranscriberAggregator : Transcriber
//    {
//        public TranscriberAggregator()
//        {
//        }

//        [JsonProperty(Order = 20)]
//        public MetadataAggregator Metadatas { get; set; }

//        [JsonProperty(Order = 23)]
//        public bool IncludeExtraTranscriptions { get; set; } = true;

//        [JsonProperty(Order = 25)]
//        public string PartSeparator { get; set; } = " | ";

//        public override bool IsPrerequisitesMet(
//            SubtitleGeneratorContext context,
//            out string reason)
//        {
//            if (Metadatas?.IsPrerequisitesMetWithoutTiming(context, out reason) == false)
//            {
//                return false;
//            }

//            reason = null;
//            return true;
//        }

//        public override void Transcribe(
//            SubtitleGeneratorContext context,
//            Transcription transcription)
//        {
//            var subtitles = BuildSubtitlesAggregation(context).ToArray();

//            // Convert to TranscribedText items
//            transcription.Items.AddRange(subtitles.Select(s => new TranscribedItem(s.StartTime, s.EndTime, MetadataCollection.CreateSimple("VoiceText", s.Text))));
//            transcription.MarkAsFinished();

//            SaveDebugSrtIfVerbose(context, transcription);
//        }

//        private IEnumerable<Subtitle> BuildSubtitlesAggregation(SubtitleGeneratorContext context)
//        {
//            // TODO
//            //var timings = context.CurrentWipsub.Transcriptions.First(f => f.Id == this.TimingsId).GetTimings();

//            //GetFinalOrders(context, out var finalTranscriptionsOrder, out var finalTranslationsOrder);

//            //var transcriptionsAnalysis = finalTranscriptionsOrder
//            //    .Select(id => context.CurrentWipsub.Transcriptions.First(t => t.Id == id).GetAnalysis(timings))
//            //    .ToArray();

//            //var extraTranscriptions = IncludeExtraTranscriptions
//            //    ? transcriptionsAnalysis
//            //        .SelectMany(ta => ta.ExtraTranscriptions.Select(tt => new ExtraTranscription(ta.Transcription.Id, tt)))
//            //        .OrderBy(item => item.TranscribedText.StartTime)
//            //        .ThenBy(item => Array.IndexOf(finalTranscriptionsOrder, item.TranscriptionId))
//            //        .ToList()
//            //    : new List<ExtraTranscription>();

//            //var result = new List<Subtitle>();

//            //foreach (var forcedTiming in timings)
//            //{
//            // TODO

//            // Emit any "extra" transcriptions occurring before (or at) current timing
//            //result.AddRange(GetExtraSubtitles(extraTranscriptions, finalTranslationsOrder, forcedTiming.StartTime));

//            //if (forcedTiming.ScreengrabText != null)
//            //{
//            //    result.Add(new Subtitle(forcedTiming.StartTime, forcedTiming.EndTime, $"{forcedTiming.ScreengrabText}"));
//            //}
//            //else if (forcedTiming.VoiceText != null)
//            //{
//            //    var builder = new StringBuilder();

//            //    foreach (var ta in transcriptionsAnalysis)
//            //    {
//            //        if (!ta.TimingsWithOverlapTranscribedTexts.TryGetValue(forcedTiming, out var overlaps) || overlaps.Length == 0)
//            //        {
//            //            builder.AppendLine($"[{ta.Transcription.Id}] ** NO TRANSCRIPTION FOUND **");
//            //        }
//            //        else
//            //        {
//            //            var index = 1;
//            //            foreach (var overlap in overlaps)
//            //            {
//            //                var number = (overlaps.Length > 1)
//            //                    ? $",{index++}/{overlaps.Length}"
//            //                    : string.Empty;

//            //                var overlapInfo = string.Empty;
//            //                var text = overlap.TranscribedText.Text;

//            //                if (ta.TranscribedTextWithOverlapTimings.TryGetValue(overlap.TranscribedText, out var overlapOtherSide)
//            //                    && overlapOtherSide.Length > 1)
//            //                {
//            //                    var matchIndex = Array.FindIndex(overlapOtherSide, x => x.Timing == forcedTiming);
//            //                    if (matchIndex >= 0)
//            //                    {
//            //                        var allTextParts = string.Join(PartSeparator, overlapOtherSide.Select(o => o.WordsText));
//            //                        overlapInfo = $"[{matchIndex + 1}/{overlapOtherSide.Length}, {allTextParts}]";
//            //                    }
//            //                    text = overlapOtherSide[matchIndex].WordsText;
//            //                }

//            //                builder.AppendLine($"[{ta.Transcription.Id}{number}] {text} {overlapInfo}");
//            //                AppendTranslationLines(builder, overlap.TranscribedText, finalTranslationsOrder);

//            //                var translations = overlap.TranscribedText
//            //                    .TranslatedTexts
//            //                    .Where(f => finalTranslationsOrder.Contains(f.Id))
//            //                    .ToArray();
//            //            }
//            //        }
//            //    }

//            //    result.Add(new Subtitle(
//            //        forcedTiming.StartTime,
//            //        forcedTiming.EndTime,
//            //        builder.ToString()));
//            //}
//            //}

//            // Flush any remaining extras after the last forced timing
//            //result.AddRange(GetExtraSubtitles(extraTranscriptions, finalTranslationsOrder, currentTiming: null));

//            return null; //  result;
//        }

//        //private void GetFinalOrders(SubtitleGeneratorContext context, out string[] finalTranscriptionsOrder, out string[] finalTranslationsOrder)
//        //{
//        //    finalTranscriptionsOrder = CreateFinalOrder(
//        //        TranscriptionsOrder ?? Array.Empty<string>(),
//        //        context.CurrentWipsub.Transcriptions.Select(f => f.Id));
//        //    finalTranslationsOrder = CreateFinalOrder(
//        //        TranslationsOrder ?? Array.Empty<string>(),
//        //        context.CurrentWipsub.Transcriptions.SelectMany(f => f.Translations.Select(f2 => f2.Id)));
//        //}

//        private static void AppendTranslationLines(
//            StringBuilder builder,
//            TranscribedItem transcribedItem,
//            string[] finalTranslationsOrder)
//        {
//            //foreach (var translation in transcribedText
//            //    .TranslatedTexts
//            //    .Where(f => finalTranslationsOrder.Contains(f.Id))
//            //    .OrderBy(f => Array.IndexOf(finalTranslationsOrder, f.Id)))
//            //{
//            //    builder.AppendLine($"   [{translation.Id}] {translation.Text}");
//            //}
//        }

//        private sealed class ExtraTranscription
//        {
//            public string TranscriptionId { get; }
//            public TranscribedItem TranscribedItem { get; }

//            public ExtraTranscription(string transcriptionId, TranscribedItem transcribedItem)
//            {
//                TranscriptionId = transcriptionId;
//                TranscribedItem = transcribedItem;
//            }
//        }

//        private static IEnumerable<Subtitle> GetExtraSubtitles(
//            List<ExtraTranscription> extraTranscriptions,
//            string[] finalTranslationsOrder,
//            TimeSpan? currentTiming)
//        {
//            while (extraTranscriptions.Count > 0 &&
//                   (currentTiming == null || extraTranscriptions.First().TranscribedItem.StartTime <= currentTiming))
//            {
//                var extra = extraTranscriptions.First();
//                extraTranscriptions.RemoveAt(0);

//                var builder = new StringBuilder();

//                builder.AppendLine("** EXTRA TRANSCRIPTION **");
//                builder.AppendLine($"[{extra.TranscriptionId}] {extra.TranscribedItem.Metadata.VoiceText}");
//                AppendTranslationLines(builder, extra.TranscribedItem, finalTranslationsOrder);

//                yield return new Subtitle(
//                    extra.TranscribedItem.StartTime,
//                    extra.TranscribedItem.EndTime,
//                    builder.ToString());
//            }
//        }
//    }
//}