using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Translations
{
    public class TranslatorAI : Translator
    {
        [JsonProperty(Order = 20, Required = Required.Always)]
        public AIEngine Engine { get; set; }
        [JsonProperty(Order = 21)]
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
                .Aggregate(context, transcription)
                .CreateRequestGenerator(translation, this.Options, transcription.Language, this.TargetLanguage);
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