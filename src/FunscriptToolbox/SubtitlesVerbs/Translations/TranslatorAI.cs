using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Translations
{
    public class TranslatorAI : Translator
    {
        [JsonProperty(Order = 20, Required = Required.Always)]
        public AIEngine Engine { get; set; }
        [JsonProperty(Order = 21, Required = Required.Always)]
        internal MetadataAggregator Metadatas { get; set; }
        [JsonProperty(Order = 22, Required = Required.Always)]
        public AIOptions Options { get; set; }
        [JsonProperty(Order = 23)]
        public string AutoMergeOn { get; set; }
        [JsonProperty(Order = 24)]
        public string AutoDeleteOn { get; set; }

        protected override string GetMetadataProduced() => this.Options.MetadataAlwaysProduced;

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

        protected override void DoWork(SubtitleGeneratorContext context)
        {
            var translation = context.WIP.Translations.FirstOrDefault(t => t.Id == this.TranslationId);
            var requestGenerator = this.Metadatas
                .Aggregate(context, translation)
                .CreateRequestGenerator(translation, this.Options, translationLanguage: this.TargetLanguage);
            var runner = new AIEngineRunner<TranslatedItem>(
                context,
                this.Engine,
                translation);

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