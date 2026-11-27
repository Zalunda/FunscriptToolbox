using Newtonsoft.Json;
using System;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public abstract class BinaryDataExtractor
    {
        [JsonIgnore]
        public abstract BinaryDataType DataType { get; }

        [ConfigDoc("The name of the field of the node in the request (ex. \"AudioClip\", \"Screenshot\"): the prompts refer to it by this name.")]
        [JsonProperty(Order = 1, Required = Required.Always)]
        public string OutputFieldName { get; set; }

        [ConfigDoc("false: nothing of this kind is added to the requests.")]
        [JsonProperty(Order = 2)]
        public bool Enabled { get; set; } = true;

        [ConfigDoc("The nodes that have this metadata (ex. \"VisualTraining\", \"AudioTraining\", set in the Properties tab of the Review editor) are also sent at the start of " +
            "the requests, with their text, as a reference (ex. to recognize the characters). null: no reference.")]
        [JsonProperty(Order = 3)]
        public string MetadataForTraining { get; set; }
        [ConfigDoc("The nodes that have this metadata don't get this data (ex. no audio clip for a node with \"GrabOnScreenText\"). null: all the nodes get it.")]
        [JsonProperty(Order = 4)]
        public string MetadataForSkipping { get; set; }

        [ConfigDoc("The text sent before the reference nodes (see MetadataForTraining).")]
        [JsonProperty(Order = 5)]
        public string TextBeforeTrainingData { get; set; } = "Character Identification Reference:";
        [ConfigDoc("The text sent after the reference nodes (see MetadataForTraining).")]
        [JsonProperty(Order = 6)]
        public string TextAfterTrainingData { get; set; } = "--------------------------------------------------";
        [ConfigDoc("Keeps the clips / screenshots made for the requests in the backup folder of the scene (also reused instead of being made again).",
            Advice = "Leave false, except to see what the AI received (also visible in the Files tab of the scene).")]
        [JsonProperty(Order = 7)]
        public bool KeepTemporaryFiles { get; set; } = false;

        [ConfigDoc("Adds 'context only' nodes (screenshots of the gaps between the subtitles) to the requests: the AI sees what happened between two lines. " +
            "In a gap: ContextShortGap after its start, then about every ContextLongGap, then ContextShortGap before its end (a short gap: one in its middle, or none).",
            Advice = "Set it to false to reduce the size of the requests, or raise the gaps to have fewer nodes added.")]
        [JsonProperty(Order = 8)]
        public bool AddContextNodes { get; set; } = false;
        [ConfigDoc("See AddContextNodes: the distance between the subtitles and the first / last context node of a gap.")]
        [JsonProperty(Order = 9)]
        public TimeSpan ContextShortGap { get; set; } = TimeSpan.FromSeconds(5);
        [ConfigDoc("See AddContextNodes: the distance between two context nodes in a long gap.")]
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