using Newtonsoft.Json;
using System;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public abstract class BinaryDataExtractor
    {
        [JsonIgnore]
        public abstract BinaryDataType DataType { get; }

        [JsonProperty(Order = 1, Required = Required.Always)]
        public string OutputFieldName { get; set; }

        [JsonProperty(Order = 2)]
        public bool Enabled { get; set; } = true;

        [JsonProperty(Order = 3)]
        public string MetadataForTraining { get; set; }
        [JsonProperty(Order = 4)]
        public string MetadataForSkipping { get; set; }

        [JsonProperty(Order = 5)]
        public string TextBeforeTrainingData { get; set; } = "Character Identification Reference:";
        [JsonProperty(Order = 6)]
        public string TextAfterTrainingData { get; set; } = "--------------------------------------------------";
        [JsonProperty(Order = 7)]
        public bool KeepTemporaryFiles { get; set; } = false;

        [JsonProperty(Order = 8)]
        public bool AddContextNodes { get; set; } = false;
        [JsonProperty(Order = 9)]
        public TimeSpan ContextShortGap { get; set; } = TimeSpan.FromSeconds(5);
        [JsonProperty(Order = 10)]
        public TimeSpan ContextLongGap { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// The times of the 'context only' nodes in a gap before an item (AddContextNodes), in order (none when not turned on):
        /// a short gap: none; a gap up to 3 x ContextShortGap: its middle; longer: ContextShortGap after its start, ContextShortGap before its end,
        /// and about every ContextLongGap between them. Used to send them (see BinaryDataExtractorExtended) and to show them (web UI).
        /// </summary>
        public TimeSpan[] GetContextTimes(ITiming gap)
        {
            if (!this.AddContextNodes)
                return Array.Empty<TimeSpan>();

            var shortGapConfig = this.ContextShortGap == TimeSpan.Zero
                ? this.ContextLongGap
                : this.ContextShortGap;
            var longGapConfig = this.ContextLongGap;

            if (gap.Duration < TimeSpan.Zero)
                return new[] { gap.StartTime + shortGapConfig };

            if (gap.Duration <= shortGapConfig)
                return Array.Empty<TimeSpan>();

            var times = new System.Collections.Generic.List<TimeSpan>();
            if (gap.Duration <= shortGapConfig + shortGapConfig + shortGapConfig)
            {
                times.Add(gap.StartTime + TimeSpan.FromMilliseconds(gap.Duration.TotalMilliseconds / 2));
            }
            else
            {
                var firstContextNodeTime = gap.StartTime + shortGapConfig;
                var lastContextNodeTime = gap.EndTime - shortGapConfig;
                times.Add(firstContextNodeTime);

                var remainingGrapDuration = lastContextNodeTime - firstContextNodeTime;
                if (remainingGrapDuration > longGapConfig)
                {
                    var currentTime = firstContextNodeTime;
                    var nbSections = Math.Floor(remainingGrapDuration.TotalMilliseconds / longGapConfig.TotalMilliseconds) + 1;
                    var sectionDuration = TimeSpan.FromMilliseconds(remainingGrapDuration.TotalMilliseconds / nbSections);
                    for (var i = 0; i < nbSections - 1; i++)
                    {
                        currentTime += sectionDuration;
                        times.Add(currentTime);
                    }
                }

                times.Add(lastContextNodeTime);
            }
            return times.ToArray();
        }
    }
}