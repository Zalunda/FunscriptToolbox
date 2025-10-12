using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscriberClone : Transcriber
    {
        public class JobState
        {
            public string SourceIdUsed { get; set; }
        }

        [JsonProperty(Order = 10, Required = Required.Always)]
        public string SourceId { get; set; }

        [JsonProperty(Order = 12)]
        public MetadataAggregator Metadatas { get; set; }

        private string _metadataProduced;
        protected override string GetMetadataProduced() => _metadataProduced;

        protected override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            var source = (TimedItemWithMetadataCollection) context.WIP.Transcriptions.FirstOrDefault(t => t.Id == this.SourceId && t.IsFinished)
                ?? context.WIP.Translations.FirstOrDefault(t => t.Id == this.SourceId && t.IsFinished);
            if (source == null || !source.IsFinished)
            {
                reason = $"Source '{this.SourceId}' has not been completed yet.";
                return false;
            }

            if (Metadatas != null && !Metadatas.Aggregate(context).IsPrerequisitesMetWithoutTimings(out reason))
            {
                return false;
            }

            reason = null;
            return true;
        }
        protected override bool NeedsToRun(SubtitleGeneratorContext context, out string reason)
        {
            var transcription = context.WIP.Transcriptions.FirstOrDefault(t => t.Id == this.TranscriptionId);
            var jobState = transcription.CurrentJobState as JobState;
            if (jobState == null || jobState.SourceIdUsed != this.SourceId)
            {
                reason = $"sourceId changed: {jobState?.SourceIdUsed} => {this.SourceId}";
                return true;
            }
            return base.NeedsToRun(context, out reason);
        }

        protected override void DoWorkInternal(SubtitleGeneratorContext context, Transcription transcription)
        {
            var source = (TimedItemWithMetadataCollection)context.WIP.Transcriptions.FirstOrDefault(t => t.Id == this.SourceId && t.IsFinished)
                ?? context.WIP.Translations.FirstOrDefault(t => t.Id == this.SourceId && t.IsFinished);
            _metadataProduced = source.MetadataAlwaysProduced;

            transcription.Items.Clear();

            var sourceItems = source.GetItems();
            var aggregatedMetadata = new MetadataAggregator()
            {
                TimingsSource = this.SourceId,
                Sources = this.Metadatas == null ? this.SourceId : this.Metadatas.Sources + $",{this.SourceId}"
            }
                .Aggregate(context, source);

            transcription.Items.AddRange(
                aggregatedMetadata.ReferenceTimingsWithMetadata.Select(item =>
                {
                    var filteredMetadata = new MetadataCollection();
                    filteredMetadata.Merge(item.Metadata, privateMetadataNames: transcription.PrivateMetadataNames);
                    return new TranscribedItem(
                        item.StartTime,
                        item.EndTime,
                        filteredMetadata);
                }));
            transcription.CurrentJobState = new JobState { SourceIdUsed = this.SourceId };
            transcription.MetadataAlwaysProduced = _metadataProduced;
            transcription.MarkAsFinished();
            context.WIP.Save();
        }
        protected override string GetWorkerTypeName() => "Clone/Alias";
        protected override string GetExecutionVerb() => "Cloning";

        protected override IEnumerable<string> GetAdditionalStatusLines(SubtitleGeneratorContext context)
        {
            yield return $"Cloned from: '{this.SourceId}'";
            foreach (var line in base.GetAdditionalStatusLines(context))
            {
                yield return line;
            }
        }
    }
}