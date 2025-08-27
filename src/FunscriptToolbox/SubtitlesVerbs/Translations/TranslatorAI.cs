using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;

namespace FunscriptToolbox.SubtitlesVerbs.Translations
{
    public class TranslatorAI : Translator
    {
        [JsonProperty(Order = 10)]
        public int MaxItemsInRequest { get; set; } = 10000;
        [JsonProperty(Order = 11)]
        public int IncludePreviousItems { get; set; } = 0;
        [JsonProperty(Order = 12)]
        public int OverlapItemsInRequest { get; set; } = 0;

        [JsonProperty(Order = 20, Required = Required.Always)]
        public AIEngine Engine { get; set; }
        [JsonProperty(Order = 21)]
        internal MetadataAggregator Metadatas { get; set; }
        [JsonProperty(Order = 22)]
        public AIOptions Options { get; set; }

        protected override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            if (GetTranscription(context) == null)
            {
                reason = $"Transcription '{this.TranscriptionId}' is not done yet.";
                return false;
            }

            this.Metadatas = this.Metadatas ?? new MetadataAggregator();
            this.Metadatas.TimingsSource = this.Metadatas.TimingsSource ?? this.TranscriptionId;
            if (this.Metadatas.Aggregate(context).IsPrerequisitesMetWithTimings(out reason) == false)
            {
                return false;
            }

            reason = null;
            return true;
        }

        protected override void Translate(
            SubtitleGeneratorContext context,
            Translation translation)
        {
            var transcription = GetTranscription(context);
            var requestGenerator = this.Metadatas
                .Aggregate(context, transcription, this.Options?.MergeRules)
                .CreateRequestGenerator(translation, this.Options, transcription.Language, this.TargetLanguage);
            var runner = new AIEngineRunner<TranslatedItem>(
                context,
                this.Engine,
                translation);

            runner.Run(requestGenerator);

            if (requestGenerator.IsFinished())
            {
                translation.MarkAsFinished();
                context.CurrentWipsub.Save();
            }

            SaveDebugSrtIfVerbose(context, translation);
        }
    }
}