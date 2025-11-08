using Newtonsoft.Json;

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
        public string TextBeforeTrainingData { get; set; } = "Character Identification Reference:";
        [JsonProperty(Order = 4)]
        public string TextAfterTrainingData { get; set; } = "--------------------------------------------------";
        [JsonProperty(Order = 5)]
        public bool KeepTemporaryFiles { get; set; } = false;
    }
}