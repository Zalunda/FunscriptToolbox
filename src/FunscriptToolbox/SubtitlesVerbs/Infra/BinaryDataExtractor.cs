using Newtonsoft.Json;
using System;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public abstract class BinaryDataExtractor
    {
        [JsonIgnore]
        public abstract BinaryDataType DataType { get; }

        [JsonProperty(Order = 1)]
        public string OutputFieldName { get; set; }

        [JsonProperty(Order = 2)]
        public string MetadataForTraining { get; set; }
        [JsonProperty(Order = 3)]
        public string MetadataForSkipping { get; set; }

        [JsonProperty(Order = 4)]
        public string TextBeforeTrainingData { get; set; } = "Character Identification Reference:";
        [JsonProperty(Order = 5)]
        public string TextAfterTrainingData { get; set; } = "--------------------------------------------------";
        [JsonProperty(Order = 6)]
        public bool KeepTemporaryFiles { get; set; } = false;

        [JsonProperty(Order = 7)]
        public bool AddContextNodes { get; set; } = false;
        [JsonProperty(Order = 8)]
        public TimeSpan ContextShortGap { get; set; } = TimeSpan.FromSeconds(5);
        [JsonProperty(Order = 9)]
        public TimeSpan ContextLongGap { get; set; } = TimeSpan.FromSeconds(30);
    }
}