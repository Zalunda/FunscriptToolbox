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
    }
}