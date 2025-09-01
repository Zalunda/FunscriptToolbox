using FunscriptToolbox.Core;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Outputs
{
    internal class SubtitleOutputSrt : SubtitleOutput
    {
        public SubtitleOutputSrt()
        {
            
        }

        public override string Description => $"{base.Description}: {this.FileSuffix}" ;

        [JsonProperty(Order = 10, Required = Required.Always)]
        public string FileSuffix { get; set; }

        public MetadataAggregator Metadatas { get; set; }


        [JsonProperty(Order = 11, Required = Required.Always)]
        public string TranscriptionId { get; set; }
        [JsonProperty(Order = 12, Required = Required.Always)]
        public string[] TranslationsOrder { get; set; }
        [JsonProperty(Order = 13)]
        public TimeSpan MinimumSubtitleDuration { get; set; } = TimeSpan.FromSeconds(1.5);
        [JsonProperty(Order = 14)]
        public TimeSpan ExpandSubtileDuration { get; set; } = TimeSpan.FromSeconds(0.5);

        [JsonProperty(Order = 20)]
        public SubtitleToInject[] SubtitlesToInject { get; set; }

        public override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            reason = $"Cannot create file because transcription '{this.TranscriptionId}' doesn't exists yet.";
            return null != (context.WIP.Transcriptions.FirstOrDefault(t => t.Id == this.TranscriptionId)
                ?? context.WIP.Transcriptions.FirstOrDefault());
        }

        public override void CreateOutput(
            SubtitleGeneratorContext context)        
        {
            // TODO
            //var transcription = context.CurrentWipsub.Transcriptions.FirstOrDefault(t => t.Id == this.TranscriptionId) 
            //    ?? context.CurrentWipsub.Transcriptions.FirstOrDefault();
            //if (transcription == null)
            //{
            //    context.WriteError($"Cannot create file '{this.FileSuffix}' because transcription '{this.TranscriptionId}' doesn't exists.");
            //    return;
            //}

            //var finalTranslationOrder = CreateFinalOrder(
            //    this.TranslationsOrder, 
            //    context.CurrentWipsub.Transcriptions.SelectMany(f => f.Translations.Select(f2 => f2.Id)));

            //var subtitleFile = new SubtitleFile();
            //foreach (var transcribedItem in transcription.Items)
            //{
            //    var builder = new StringBuilder();
            //    builder.AppendLine(transcribedItem.Text);
            //    foreach (var translatedText in transcribedText
            //        .TranslatedTexts
            //        .Where(f => finalTranslationOrder.Contains(f.Id))
            //        .OrderBy(f => Array.IndexOf(finalTranslationOrder, f.Id)))
            //    {
            //        builder.AppendLine($"   [{translatedText.Id}] {translatedText.Text}");
            //    }
            //    subtitleFile.Subtitles.Add(new Subtitle(transcribedText.StartTime, transcribedText.EndTime, builder.ToString()));
            //}

            //// Apply minimum duration and expansion
            //subtitleFile.ExpandTiming(this.MinimumSubtitleDuration, this.ExpandSubtileDuration);

            //subtitleFile.Subtitles.AddRange(
            //    GetAdjustedSubtitlesToInject(
            //        subtitleFile.Subtitles,
            //        this.SubtitlesToInject,
            //        context.CurrentWipsub.PcmAudio.Duration));

            //var filename = context.CurrentBaseFilePath + this.FileSuffix;
            //context.SoftDelete(filename);
            //subtitleFile.SaveSrt(filename);
        }


        //private IEnumerable<TranscribedItem> BuildCandidatesAggregation(SubtitleGeneratorContext context)
        //{
        //    var aggregation = this.Metadatas.Aggregate(context);
        //    var timings = aggregation.ReferenceTimingsWithMetadata;
        //    var (othersSourcesRaw, _) = this.Metadatas.GetOthersSources(context, this.CandidatesSources);
        //    var othersSources = othersSourcesRaw.Select(osr => osr.GetAnalysis(timings)).ToArray();

        //    foreach (var timing in timings)
        //    {
        //        var sb = new StringBuilder();
        //        foreach (var otherSource in othersSources)
        //        {
        //            if (this.IncludeExtraItems)
        //            {
        //                // Emit any "extra" transcriptions occurring before(or at) current timing for that source
        //                // Note: I don't care if multiple subtitle timing overlap in that case.
        //                while (otherSource.ExtraItems.FirstOrDefault()?.StartTime <= timing.StartTime)
        //                {
        //                    var extra = otherSource.ExtraItems.First();
        //                    var value = extra.Metadata.Get(otherSource.Container.MetadataAlwaysProduced);
        //                    if (value != null)
        //                    {
        //                        yield return new TranscribedItem(
        //                            extra.StartTime,
        //                            extra.EndTime,
        //                            MetadataCollection.CreateSimple(this.MetadataProduced, $"EXTRA: [{otherSource.Container.Id}] {value}"));
        //                        otherSource.ExtraItems.Remove(extra);
        //                    }
        //                }
        //            }

        //            if (otherSource.TimingsWithOverlapItems.TryGetValue(timing, out var overlaps))
        //            {
        //                var indexOverlap = 1;
        //                foreach (var overlap in overlaps)
        //                {
        //                    var suffixeOverlap = overlaps.Length > 1 ? $" ({indexOverlap++}/{overlaps.Length})" : string.Empty;

        //                    var text = overlap.Item.Metadata.Get(otherSource.Container.MetadataAlwaysProduced);
        //                    var overlapInfo = string.Empty;

        //                    if (otherSource.ItemsWithOverlapTimings.TryGetValue(overlap.Item, out var overlapOtherSide)
        //                        && overlapOtherSide.Length > 1)
        //                    {
        //                        var matchIndex = Array.FindIndex(overlapOtherSide, x => x.Timing == timing);
        //                        if (matchIndex >= 0)
        //                        {
        //                            var allTextParts = string.Join(PartSeparator, overlapOtherSide.Select(o => o.WordsText));
        //                            overlapInfo = $"[{matchIndex + 1}/{overlapOtherSide.Length}, {allTextParts}]";
        //                        }
        //                        text = overlapOtherSide[matchIndex].WordsText;
        //                    }

        //                    sb.AppendLine($"[{otherSource.Container.Id}{suffixeOverlap}] {text} {overlapInfo}");
        //                }
        //            }
        //        }
        //        yield return new TranscribedItem(
        //            timing.StartTime,
        //            timing.EndTime,
        //            MetadataCollection.CreateSimple(this.MetadataProduced, sb.ToString()));
        //    }
        //}

    }
}
