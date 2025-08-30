using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class MetadataAggregation
    {
        private readonly string r_timingSource;
        private readonly string[] r_reasonsFromSourcesReferences;
        public TimedItemWithMetadata[] ReferenceTimingsWithMetadata { get; }

        public MetadataAggregation(
            string timingSource,
            string[] reasonsFromSourcesReferences,
            TimedItemWithMetadata[] referenceTimingsWithMetadata)
        {
            r_timingSource = timingSource;
            r_reasonsFromSourcesReferences = reasonsFromSourcesReferences;
            this.ReferenceTimingsWithMetadata = referenceTimingsWithMetadata;
        }

        public bool IsPrerequisitesMetWithTimings(out string reason) => IsPrerequisitesMet(out reason, true);
        public bool IsPrerequisitesMetWithoutTimings(out string reason) => IsPrerequisitesMet(out reason, false);
        public bool IsPrerequisitesMetOnlyTimings(out string reason) => IsPrerequisitesMet(out reason, true, true);

        private bool IsPrerequisitesMet(
            out string reason,
            bool timingsRequired = false,
            bool onlyTimings = false)
        {
            var reasons = new List<string>(onlyTimings ? Array.Empty<string>() : r_reasonsFromSourcesReferences);
            if (timingsRequired && r_timingSource == null)
            {
                reasons.Insert(0, $"TimingSource haven't been set Metadatas section in config file.");
            }
            else if (timingsRequired && this.ReferenceTimingsWithMetadata == null)
            {
                reasons.Insert(0, $"Transcription '{r_timingSource}' is not done yet (for timings).");
            }

            if (reasons.Any())
            {
                reason = (reasons.Count > 0)
                    ? "\n" + string.Join("\n", reasons)
                    : reasons.First();
                return false;
            }

            reason = null;
            return true;
        }

        internal AIRequestGenerator CreateRequestGenerator(
            TimedItemWithMetadataCollection workingOnContainer,
            AIOptions options = null, 
            Language transcriptionLanguage = null,
            Language translationLanguage = null)
        {
            return new AIRequestGenerator(
                this.ReferenceTimingsWithMetadata,
                workingOnContainer,
                transcriptionLanguage,
                translationLanguage,
                options);
        }
    }
}