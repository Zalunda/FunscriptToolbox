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
            var sourceTranscription = context.WIP.Transcriptions.FirstOrDefault(t => t.Id == this.SourceId && t.IsFinished);
            if (sourceTranscription == null || !sourceTranscription.IsFinished)
            {
                reason = $"Source transcription '{this.SourceId}' has not been completed yet.";
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
            var sourceTranscription = context.WIP.Transcriptions.First(t => t.Id == this.SourceId);
            _metadataProduced = sourceTranscription.MetadataAlwaysProduced;

            transcription.Items.Clear();

            var sourceItems = sourceTranscription.GetItems();
            var aggregatedMetadata = new MetadataAggregator()
            {
                TimingsSource = this.SourceId,
                Sources = this.Metadatas == null ? this.SourceId : this.Metadatas.Sources + $",{this.SourceId}"
            }
                .Aggregate(context, sourceTranscription);

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