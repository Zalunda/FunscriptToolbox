using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class AIOptions
    {
        [ConfigDoc("The system prompt of the requests: the instructions to the AI (usually a shared AIPrompt, { \"$ref\": \"<id>\" }).")]
        [JsonProperty(Order = 2)]
        public AIPrompt SystemPrompt { get; set; }
        [ConfigDoc("The user prompt, sent just before the nodes of each request (usually a reminder of the most important rules).")]
        [JsonProperty(Order = 3)]
        public AIPrompt UserPrompt { get; set; }

        [ConfigDoc("Which nodes are sent to the AI: the metadata they must have, ',' = and (ex. \"VoiceText|OnScreenText,!SkipTranslation\"). " +
            "A rule is a regular expression on the names of the metadata (ex. \"VoiceText|OnScreenText\": one or the other; not anchored: \"VoiceText\" also matches \"VoiceTextDraft\");" +
            "'!' + an exact name = must not have it. Empty: all the nodes.")]
        [JsonProperty(Order = 4, Required = Required.Always)]
        public string MetadataNeeded { get; set; }
        [ConfigDoc("The metadata that the AI always returns for a node (ex. \"VoiceText\", \"TranslatedText\"): a node that has it is done " +
            "(not sent again when the step is resumed). ',' = several.")]
        [JsonProperty(Order = 5, Required = Required.Always)]
        public string MetadataAlwaysProduced { get; set; }
        [ConfigDoc("What is added to each node of the requests: audio clips (BinaryDataExtractorAudio), screenshots (BinaryDataExtractorImage).")]
        [JsonProperty(Order = 7)]
        public BinaryDataExtractor[] BinaryDataExtractors { get; set; }

        [ConfigDoc("How many requests are prepared and sent together (ex. in one batch of the provider). More than 1 needs NbContextItems = 0 / null " +
            "(a request can't have the answers of the one before it as context).")]
        [JsonProperty(Order = 9)]
        public int MaximumRequestsInBatch { get; set; } = 1;

        [ConfigDoc("The maximum number of nodes in one request.",
            Advice = "Smaller requests (ex. 100 - 150 nodes) give the AI more attention per node, and lose less when a request fails; bigger requests cost less (the system prompt is sent fewer times).")]
        [JsonProperty(Order = 10)]
        public int BatchSize { get; set; } = 100000;
        [ConfigDoc("When a request would be cut at BatchSize: it's cut at the largest gap between two nodes among the last N nodes instead (0: cut exactly at BatchSize).",
            Advice = "Ex. 10 - 20: the requests are cut between two conversations instead of in the middle of one.")]
        [JsonProperty(Order = 11)]
        public int BatchSplitWindows { get; set; } = 0;
        [ConfigDoc("When a request would be cut between two nodes closer than this, the next nodes are added to it until a gap of at least this duration (null: not used).")]
        [JsonProperty(Order = 12)]
        public TimeSpan? BatchKeepTogetherThreshold { get; set; }
        [ConfigDoc("A request ends sooner when it has more than this number of images (null: no limit). Never less than NbItemsMinimumReceivedToContinue nodes.")]
        [JsonProperty(Order = 13)]
        public int? BatchMaxImages { get; set; }
        [ConfigDoc("A request ends sooner when its audio clips are longer than this, all together (null: no limit). Never less than NbItemsMinimumReceivedToContinue nodes.")]
        [JsonProperty(Order = 14)]
        public TimeSpan? BatchMaxAudioDuration { get; set; }
        [ConfigDoc("A request ends sooner when it's bigger than this (MB, estimated; null: no limit). Never less than NbItemsMinimumReceivedToContinue nodes.",
            Advice = "Useful when the provider has a size limit per request (ex. a request with many images refused as too big).")]
        [JsonProperty(Order = 15)]
        public double? BatchMaxMBSize { get; set; }

        [ConfigDoc("How many nodes already done (before the nodes of the request) are sent as context, with their answers: the AI knows what happened before. 0 / null: none.",
            Advice = "Some context keeps the translation / analysis consistent from one request to the next (ex. 10 - 15); more context = more tokens.")]
        [JsonProperty(Order = 16)]
        public int? NbContextItems { get; set; } = 100000;
        [ConfigDoc("When the AI answers for fewer nodes than this (for a request), the step stops with an error instead of sending the next requests (an answer cut, or a refusal, would waste tokens).\n" +
            "Also the minimum number of nodes in a request before BatchMaxImages / BatchMaxAudioDuration / BatchMaxMBSize can end it.")]
        [JsonProperty(Order = 17)]
        public int NbItemsMinimumReceivedToContinue { get; set; } = 50;
        [ConfigDoc("The times written in each node, besides its metadata: \"StartTime\" (always written), \"EndTime\", \"Duration\"; several: \"StartTime, EndTime\".")]
        [JsonProperty(Order = 18)]
        public NodeFields FieldsToInclude { get; set; } = NodeFields.StartTime | NodeFields.EndTime;
        [ConfigDoc("For the context nodes (see NbContextItems): a metadata (or \"StartTime\", \"EndTime\", \"Duration\") is written only in the N context nodes closest to the request " +
            "(ex. { \"VoiceText\": 10, \"VoiceTextDraft\": 0 }: the VoiceText of the last 10 context nodes only, never VoiceTextDraft). Not in the list: in all the context nodes.",
            Advice = "Keeps a long context cheap: the old nodes with only what's useful far back (ex. their translation), the recent ones with everything.")]
        [JsonProperty(Order = 19)]
        public Dictionary<string, int> MetadataInContextLimits { get; set; }

        [ConfigDoc("The text written before the context nodes (the nodes already done, see NbContextItems), in the user part of the request.")]
        [JsonProperty(Order = 22)]
        public string TextBeforeContextData { get; set; } = "Context from preceding nodes:";
        [ConfigDoc("The text written after the context nodes.")]
        [JsonProperty(Order = 23)]
        public object TextAfterContextData { get; set; } = "--------------------------------------------------";
        [ConfigDoc("The text written before the nodes to do (the ones the AI must answer for).")]
        [JsonProperty(Order = 24)]
        public string TextBeforeAnalysis { get; set; } = "Begin Node Analysis:";
        [ConfigDoc("The text written after the nodes to do, at the end of the request (null: nothing).",
            Advice = "The last thing the AI reads: a short reminder there has a strong effect (ex. against a refusal).")]
        [JsonProperty(Order = 25)]
        public string TextAfterAnalysis { get; set; } = null;
    }
}