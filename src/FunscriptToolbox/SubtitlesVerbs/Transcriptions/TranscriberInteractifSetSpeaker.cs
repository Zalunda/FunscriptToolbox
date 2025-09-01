using FunscriptToolbox.SubtitlesVerbs.Infra;
using FunscriptToolbox.UI;
using FunscriptToolbox.UI.SpeakerCorrection;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscriberInteractifSetSpeaker : Transcriber
    {
        [JsonProperty(Order = 10, Required = Required.Always)]
        internal MetadataAggregator Metadatas { get; set; }
        [JsonProperty(Order = 11, Required = Required.Always)]
        public string MetadataNeeded { get; set; }
        [JsonProperty(Order = 12, Required = Required.Always)]
        public string MetadataProduced { get; set; }
        [JsonProperty(Order = 12)]
        public string MetadataPotentialSpeakers { get; set; }
        [JsonProperty(Order = 13)]
        public string MetadataDetectedSpeaker { get; set; }

        protected override string GetMetadataProduced() => null;

        protected override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context, 
            out string reason)
        {
            if (this.Metadatas.Aggregate(context).IsPrerequisitesMetWithoutTimings(out reason) == false)
            {
                return false;
            }

            reason = null;
            return true;
        }

        protected override void Transcribe(
            SubtitleGeneratorContext context,
            Transcription transcription)
        {
            var processStartTime = DateTime.Now;

            var aggregation = this.Metadatas.Aggregate(context);
            var requestGenerator = aggregation.CreateRequestGenerator(
                transcription,
                new AIOptions()
                {
                    MetadataNeeded = this.MetadataNeeded,
                    MetadataAlwaysProduced = this.MetadataProduced
                },
                transcription.Language);

            var (allItems, itemsToDo, _, _) = requestGenerator.AnalyzeItemsState();


            var nbUpdate = 1;
            void UpdateAndSave(SpeakerCorrectionWorkItem workItem)
            {
                if (workItem.FinalSpeaker != null)
                {
                    transcription.Items.Add(
                        new TranscribedItem(
                            workItem.StartTime,
                            workItem.EndTime,
                            MetadataCollection.CreateSimple(this.MetadataProduced, workItem.FinalSpeaker)));
                }
                if (nbUpdate++ % 10 == 0)
                {
                    context.WIP.Save();
                }
            }
            void Undo(SpeakerCorrectionWorkItem workItem)
            {
                transcription.Items.RemoveAll(item => item.StartTime == workItem.StartTime);
                context.WIP.Save();
            }

            // TODO Low priority, allows to save the left, right, center in the output (i.e. the key we used while doing it, if we always set those key relative to the position of characters)

            var nbItemsToDoBefore = itemsToDo.Length;
            var watch = Stopwatch.StartNew();
            Test.SpeakerCorrection(
                Path.GetFullPath(context.WIP.OriginalVideoPath),
                CreateWorkItem(
                    allItems,
                    itemsToDo),
                UpdateAndSave,
                Undo);
            watch.Stop();

            var (_, itemsToDoAfter, _, _) = requestGenerator.AnalyzeItemsState();
            transcription.Costs.Add(
                new Cost(
                    "InteractifSetSpeaker",
                    watch.Elapsed,
                    nbItemsToDoBefore - itemsToDoAfter.Length));

            if (requestGenerator.IsFinished())
            {
                transcription.MarkAsFinished();
            }
            context.WIP.Save();

            SaveDebugSrtIfVerbose(context, transcription);
        }

        private IEnumerable<SpeakerCorrectionWorkItem> CreateWorkItem(
            TimedItemWithMetadataTagged[] allItems,
            TimedItemWithMetadataTagged[] itemsToDo)
        {
            string potentialSpeakers = null;
            foreach (var item in allItems)
            {
                potentialSpeakers = item.Metadata.Get(this.MetadataPotentialSpeakers) ?? potentialSpeakers;
                if (itemsToDo.Contains(item))
                {
                    yield return new SpeakerCorrectionWorkItem(
                        item.StartTime,
                        item.EndTime,
                        potentialSpeakers?.Split(',').Select(f => CleanName(f)) ?? Array.Empty<string>(),
                        CleanName(item.Metadata.Get(this.MetadataDetectedSpeaker)));
                }
            }
        }

        private string CleanName(string name)
        {
            if (name == null)
                return null;
            var index = name.IndexOf('(');
            return index < 0 ? name : name.Substring(0, index).Trim();
        }
    }
}