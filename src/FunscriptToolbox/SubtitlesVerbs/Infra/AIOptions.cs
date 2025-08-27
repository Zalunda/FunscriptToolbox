using Newtonsoft.Json;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class AIOptions
    {
        [JsonProperty(Order = 1)]
        public Dictionary<string, string> MergeRules { get; set; }

        [JsonProperty(Order = 2)]
        public AIPrompt SystemPrompt { get; set; }

        [JsonProperty(Order = 3)]
        public AIPrompt FirstUserPrompt { get; set; }

        [JsonProperty(Order = 4)]
        public AIPrompt OtherUserPrompt { get; set; }

        [JsonProperty(Order = 5)]
        public string MetadataNeeded { get; set; }
        [JsonProperty(Order = 6)]
        public string MetadataProduced { get; set; }
        [JsonProperty(Order = 7)]
        public string MetadataForTraining { get; set; }
        [JsonProperty(Order = 8)]
        public bool SendAllItemsToAI { get; set; } = true;

        [JsonProperty(Order = 10)]
        public int BatchSize { get; set; } = 100000;
        [JsonProperty(Order = 11)]
        public int MinimumItemsAddedToContinue { get; set; } = 1000;
    }

    public class AIOptionsForAudioTranscription : AIOptions
    {
        public AIOptionsForAudioTranscription()
        {
            MergeRules = new Dictionary<string, string>
            {
                {"VoiceText", null }
            };

            MetadataNeeded = "!NoVoice,!OnScreenText,!GrabOnScreenText";
            MetadataProduced = "VoiceText";
            MetadataForTraining = "SpeakerTraining";
        }
    }

    public class AIOptionsForOnScreenText : AIOptions
    {
        public AIOptionsForOnScreenText()
        {
            MergeRules = new Dictionary<string, string>
            {
                {"OnScreenText", null }
            };

            MetadataNeeded = "GrabOnScreenText";
            MetadataProduced = "OnScreenText";
            SendAllItemsToAI = false;
        }
    }

    public class AIOptionsForTranslation : AIOptions
    {
        public AIOptionsForTranslation()
        {
            MergeRules = new Dictionary<string, string>
            {
                {"TranslatedText", null }
            };

            MetadataNeeded = "VoiceText|OnScreenText";
            MetadataProduced = "TranslatedText";
        }
    }

    public class AIOptionsForRefineTranslation : AIOptions
    {
        public AIOptionsForRefineTranslation()
        {
            MergeRules = new Dictionary<string, string>
            {
                {"TranslatedText", "OriginalTranslatedText" }
            };

            MetadataNeeded = "VoiceText|OnScreenText";
            MetadataProduced = "TranslatedText";
        }
    }
}