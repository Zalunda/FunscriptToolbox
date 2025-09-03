using FunscriptToolbox.SubtitlesVerbs.Infra;
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
        [JsonProperty(Order = 20, Required = Required.Always)]
        public MetadataAggregator Metadatas { get; set; }
        [JsonProperty(Order = 21, Required = Required.Always)]
        public string[] CandidatesSources { get; set; }
        [JsonProperty(Order = 22, Required = Required.Always)]
        public string MetadataProduced { get; set; }
        [JsonProperty(Order = 23)]
        public bool WaitForFinished { get; set; } = true;

        [JsonProperty(Order = 32)]
        public bool IncludeExtraItems { get; set; } = true;

        [JsonProperty(Order = 33)]
        public string PartSeparator { get; set; } = " | ";

        public override bool CanBeUpdated => !this.WaitForFinished;

        protected override string GetMetadataProduced() => this.MetadataProduced;

        protected override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            var aggregate = this.Metadatas.Aggregate(context);
            if (aggregate.IsPrerequisitesMetWithTimings(out reason) == false)
            {
                return false;
            }

            if (this.WaitForFinished)
            {
                var (_, reasons) = this.Metadatas.GetOthersSources(context, this.CandidatesSources);
                if (reasons.Any() == true)
                {
                    reason = (reasons.Length > 0)
                            ? "\n" + string.Join("\n", reasons)
                            : reasons.First();
                    return false;
                }
            }
            else
            {
                if (aggregate.IsPrerequisitesMetOnlyTimings(out reason) == false)
                {
                    return false;
                }
            }

            reason = null;
            return true;
        }

        protected override void Transcribe(
            SubtitleGeneratorContext context,
            Transcription transcription)
        {
            transcription.Items.Clear();
            transcription.Items.AddRange(BuildCandidatesAggregation(context));
            transcription.MarkAsFinished();
        }

        private IEnumerable<TranscribedItem> BuildCandidatesAggregation(SubtitleGeneratorContext context)
        {
            var aggregation = this.Metadatas.Aggregate(context);
            var timings = aggregation.ReferenceTimingsWithMetadata;
            var (othersSourcesRaw, _) = this.Metadatas.GetOthersSources(context, this.CandidatesSources);
            var othersSources = othersSourcesRaw.Select(osr => osr.GetAnalysis(timings)).ToArray();

            foreach (var timing in timings)
            {
                var sb = new StringBuilder();
                foreach (var otherSource in othersSources)
                {
                    if (this.IncludeExtraItems)
                    {
                        // Emit any "extra" transcriptions occurring before(or at) current timing for that source
                        // Note: I don't care if multiple subtitle timing overlap in that case.
                        while (otherSource.ExtraItems.FirstOrDefault()?.StartTime <= timing.StartTime)
                        {
                            var extra = otherSource.ExtraItems.First();
                            var value = extra.Metadata.Get(otherSource.Container.MetadataAlwaysProduced);
                            if (value != null)
                            {
                                yield return new TranscribedItem(
                                    extra.StartTime,
                                    extra.EndTime,
                                    MetadataCollection.CreateSimple(this.MetadataProduced, $"EXTRA: [{otherSource.Container.Id}] {value}"));
                                otherSource.ExtraItems.Remove(extra);
                            }
                        }
                    }

                    if (otherSource.TimingsWithOverlapItems.TryGetValue(timing, out var overlaps))
                    {
                        var indexOverlap = 1;
                        foreach (var overlap in overlaps)
                        {
                            var suffixeOverlap = overlaps.Length > 1 ? $" ({indexOverlap++}/{overlaps.Length})" : string.Empty;

                            var text = overlap.Item.Metadata.Get(otherSource.Container.MetadataAlwaysProduced);
                            var overlapInfo = string.Empty;

                            if (otherSource.ItemsWithOverlapTimings.TryGetValue(overlap.Item, out var overlapOtherSide)
                                && overlapOtherSide.Length > 1)
                            {
                                var matchIndex = Array.FindIndex(overlapOtherSide, x => x.Timing == timing);
                                if (matchIndex >= 0)
                                {
                                    var allTextParts = string.Join(PartSeparator, overlapOtherSide.Select(o => o.WordsText));
                                    overlapInfo = $"[{matchIndex + 1}/{overlapOtherSide.Length}, {allTextParts}]";
                                }
                                text = overlapOtherSide[matchIndex].WordsText;
                            }

                            sb.AppendLine($"[{otherSource.Container.Id}{suffixeOverlap}] {text} {overlapInfo}");
                        }
                    }
                }
                yield return new TranscribedItem(
                    timing.StartTime,
                    timing.EndTime,
                    MetadataCollection.CreateSimple(this.MetadataProduced, sb.ToString()));
            }
        }
    }
}