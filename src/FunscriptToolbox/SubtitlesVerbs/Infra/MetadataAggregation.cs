using FunscriptToolbox.SubtitlesVerbs.Transcriptions;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class MetadataAggregation
    {
        private readonly string r_timingSource;
        private readonly TimedItemWithMetadataCollection[] r_rawSourceReference;
        private readonly string[] r_reasonsFromSourcesReferences;
        private readonly TimedItemWithMetadata[] r_referenceTimingsWithMetadata;

        public MetadataAggregation(
            string timingSource,
            TimedItemWithMetadataCollection[] rawSourceReferences,
            string[] reasonsFromSourcesReferences,
            TimedItemWithMetadata[] referenceTimingsWithMetadata)
        {
            r_timingSource = timingSource;
            r_rawSourceReference = rawSourceReferences;
            r_reasonsFromSourcesReferences = reasonsFromSourcesReferences;
            r_referenceTimingsWithMetadata = referenceTimingsWithMetadata;
        }

        public bool IsPrerequisitesMetWithTimings(out string reason) => IsPrerequisitesMet(out reason, true);
        public bool IsPrerequisitesMetWithoutTimings(out string reason) => IsPrerequisitesMet(out reason, false);

        private bool IsPrerequisitesMet(
            out string reason,
            bool timingsRequired = false)
        {
            var reasons = new List<string>(r_reasonsFromSourcesReferences);
            if (timingsRequired && r_referenceTimingsWithMetadata == null)
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

        internal AIRequestGenerator CreateRequestGenerator(Transcription transcription, AIOptions options = null, Language translationLanguage = null)
        {
            return new AIRequestGenerator(
                r_referenceTimingsWithMetadata,
                transcription,
                translationLanguage,
                options);
        }
    }
}