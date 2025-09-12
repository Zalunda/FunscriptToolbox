using Newtonsoft.Json;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class AIOptions
    {

        [JsonProperty(Order = 2)]
        public AIPrompt SystemPrompt { get; set; }
        [JsonProperty(Order = 3)]
        public AIPrompt UserPrompt { get; set; }

        [JsonProperty(Order = 4, Required = Required.Always)]
        public string MetadataNeeded { get; set; }
        [JsonProperty(Order = 5, Required = Required.Always)]
        public string MetadataAlwaysProduced { get; set; }
        [JsonProperty(Order = 6)]
        public string MetadataForTraining { get; set; }

        [JsonProperty(Order = 10)]
        public int BatchSize { get; set; } = 100000;
        [JsonProperty(Order = 11)]
        public int BatchSplitWindows { get; set; } = 0;
        [JsonProperty(Order = 12)]
        public int? NbContextItems { get; set; } = 100000;
        [JsonProperty(Order = 13)]
        public int NbItemsMinimumReceivedToContinue { get; set; } = 50;
        [JsonProperty(Order = 14)]
        public int NbItemsMaximumForTraining { get; set; } = 100000;

        [JsonProperty(Order = 20)]
        public string TextBeforeTrainingData { get; set; } = "Character Identification Reference:";
        [JsonProperty(Order = 21)]
        public string TextAfterTrainingData { get; set; } = "--------------------------------------------------";
        [JsonProperty(Order = 22)]
        public string TextBeforeContextData { get; set; } = "Context from preceding nodes:";
        [JsonProperty(Order = 23)]
        public object TextAfterContextData { get; set; } = "--------------------------------------------------";
        [JsonProperty(Order = 24)]
        public string TextBeforeAnalysis { get; set; } = "Begin Node Analysis:";
        [JsonProperty(Order = 25)]
        public string TextAfterAnalysis { get; set; } = null;
    }
}