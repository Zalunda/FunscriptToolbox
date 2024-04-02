using FunscriptToolbox.SubtitlesVerbV2;
using System;
using Newtonsoft.Json;

namespace FunscriptToolbox.SubtitlesVerbsV2.Outputs
{
    internal class SubtitleOutputCostReport : SubtitleOutput
    {
        public SubtitleOutputCostReport()
        {

        }

        public override bool NeedSubtitleForcedTimings => false;

        [JsonProperty(Order = 10)]
        public string FileSuffixe { get; set; }
        [JsonProperty(Order = 11)]
        public CostReportLevel Level { get; set; }

        public override void CreateOutput(
            SubtitleGeneratorContext context,
            WorkInProgressSubtitles wipsub)
        {
            // TODO Add info/verbose logs
            // TODO cleanup/improve text in generated srt, especially % for text that overlap multiple forced timing.

            if (this.FileSuffixe == null)
            {
                throw new ArgumentNullException($"{typeof(SubtitleOutputCostReport).Name}.FileSuffixe");
            }

            // TODO
        }
    }
}
