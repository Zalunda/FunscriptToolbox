using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Translations
{
    public class TranslatorAI : Translator
    {
        [ConfigDoc("The AI engine of the step (usually a shared engine, ex. { \"$ref\": \"AIEngineGemini3Pro\" }: an AIEngineCollection with its fallbacks).",
            Advice = "Choose another engine with ✎ (the shared engines are listed), ex. a Claude or GPT engine for the translation.")]
        [JsonProperty(Order = 20, Required = Required.Always)]
        public AIEngine Engine { get; set; }
        [ConfigDoc("Changes merged into the body of the requests of this step only, in \"RequestBodyExtension\" (ex. { \"RequestBodyExtension\": { \"generationConfig\": { ... } } }): " +
            "the engine stays shared. Any other property here is an error.")]
        [JsonProperty(Order = 21)]
        public AIEngineConfigOverride EngineConfigOverride { get; set; }
        [ConfigDoc("Which subtitles are translated (TimingsSource) and which metadata of the other steps are attached to them (Sources: the transcription, the analysis, the speakers, the context...).")]
        [JsonProperty(Order = 22, Required = Required.Always)]
        internal MetadataAggregator Metadatas { get; set; }
        [ConfigDoc("The requests: the prompts, which subtitles are sent (MetadataNeeded), the batching, the context (NbContextItems).")]
        [JsonProperty(Order = 23, Required = Required.Always)]
        public AIOptions Options { get; set; }
        [ConfigDoc("When the answer of a subtitle contains this text, the subtitle is merged with the one before it (the AI says that it goes with the previous line). null: not done.")]
        [JsonProperty(Order = 24)]
        public string AutoMergeOn { get; set; }
        [ConfigDoc("With AutoMergeOn: when the answer of a subtitle contains this text, the subtitle is removed.")]
        [JsonProperty(Order = 25)]
        public string AutoDeleteOn { get; set; }
        [ConfigDoc("When the answer of the AI can't be read (ex. broken JSON), the web UI asks you to fix the text (false: an error right away).")]
        [JsonProperty(Order = 26)]
        public bool PromptUserOnError { get; set; } = true;

        protected override string GetMetadataProduced() => this.Options.MetadataAlwaysProduced;

        protected override void DescribeDataFlow(WorkerDataFlow flow)
        {
            base.DescribeDataFlow(flow);
            flow.InputMetadatas(this.Metadatas);
            flow.InputExtractors(this.Options?.BinaryDataExtractors);
        }

        protected override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            if (this.Metadatas.Aggregate(context).IsPrerequisitesMetWithTimings(out reason) == false)
            {
                return false;
            }

            reason = null;
            return true;
        }

        protected override void DoWorkInternal(SubtitleGeneratorContext context, Translation translation)
        {
            var requestGenerator = this.Metadatas
                .Aggregate(context, translation)
                .CreateRequestGenerator(translation, this.Options, translationLanguage: this.TargetLanguage);
            var runner = new AIEngineRunner<TranslatedItem>(
                context,
                this.Engine,
                this.EngineConfigOverride,
                translation,
                this.PromptUserOnError);

            runner.Run(requestGenerator);

            if (requestGenerator.IsFinished())
            {
                if (this.AutoMergeOn != null)
                {
                    var originalItems = translation.Items;
                    var newItems = new List<TranslatedItem>();
                    TranslatedItem currentItem = null;
                    foreach (var item in originalItems)
                    {
                        if (currentItem != null && item.Metadata.Get(this.GetMetadataProduced()).Contains(this.AutoMergeOn))
                        {
                            currentItem = new TranslatedItem(
                                currentItem.StartTime,
                                item.EndTime,
                                currentItem.Metadata);
                        }
                        else if (currentItem != null && item.Metadata.Get(this.GetMetadataProduced()).Contains(this.AutoDeleteOn))
                        {
                            // Ignore node
                        }
                        else
                        {
                            if (currentItem != null)
                            {
                                newItems.Add(currentItem);
                            }
                            currentItem = item;
                        }
                    }
                    if (currentItem != null)
                    {
                        newItems.Add(currentItem);
                    }
                    translation.Items.Clear();
                    translation.Items.AddRange(newItems);
                }

                translation.MarkAsFinished();
                context.WIP.Save();
            }
        }
    }
}