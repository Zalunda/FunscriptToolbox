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
        public bool WarnIfNoPotentialSpeakersProvided { get; set; } = true;
        [JsonProperty(Order = 14)]
        public string MetadataDetectedSpeaker { get; set; }

        protected override string GetMetadataProduced() => this.MetadataProduced;

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

            var (allItems, itemsToDo, itemsAlreadyDone, _) = requestGenerator.AnalyzeItemsState();

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

            var nbItemsToDoBefore = itemsToDo.Length;
            var workItems = CreateWorkItem(allItems, itemsToDo.Union(itemsAlreadyDone).ToArray()).ToArray();

            if (this.WarnIfNoPotentialSpeakersProvided && !workItems.Any(f => f.PotentialSpeakers.Count > 0))
            {
                // No potential speakers instruction was given, warn the user and quit
                context.WriteError($"No '{this.MetadataPotentialSpeakers}' provided in referenced metadatas.");
                context.AddUserTodo($"Add '{this.MetadataPotentialSpeakers}' in referenced metadatas.");
                return;
            }

            if (itemsAlreadyDone.Length == 0)
            {
                var autoSpeakerChoices = workItems.Select(f => f.PotentialSpeakers.Count == 1 ? f.PotentialSpeakers[0] : null).Distinct().ToArray();
                if (itemsAlreadyDone.Length == 0 && autoSpeakerChoices.Length == 1 && autoSpeakerChoices[0] != null)
                {
                    // Every item would have been marked with the same speaker, so we just mark the job as done and quit (without adding redundant metadata)
                    transcription.MarkAsFinished();
                    context.WIP.Save();
                    return;
                }
            }

            // Auto select all items that have only one potential speaker
            foreach (var item in workItems)
            {
                if (item.FinalSpeaker == null && item.PotentialSpeakers?.Count == 1)
                {
                    item.FinalSpeaker = item.PotentialSpeakers[0];
                    UpdateAndSave(item);
                }
            }

            var watch = Stopwatch.StartNew();
            Test.SpeakerCorrection(
                workItems,
                time => { 
                    var (filename, adjustedTime) = context.WIP.TimelineMap.GetPathAndPosition(time); 
                    return (Path.Combine(context.WIP.ParentPath, filename), adjustedTime); 
                },
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
        }

        private IEnumerable<SpeakerCorrectionWorkItem> CreateWorkItem(
            IEnumerable<TimedItemWithMetadataTagged> allItems,
            TimedItemWithMetadataTagged[] itemsToDo)
        {
            string[] potentialSpeakers = null;
            foreach (var item in allItems)
            {
                potentialSpeakers = item.Metadata.Get(this.MetadataPotentialSpeakers)?.Split(',').Select(f => CleanName(f)).ToArray()
                    ?? potentialSpeakers;
                if (itemsToDo.Contains(item))
                {
                    yield return new SpeakerCorrectionWorkItem(
                        item.StartTime,
                        item.EndTime,
                        potentialSpeakers ?? Array.Empty<string>(),
                        CleanName(item.Metadata.Get(this.MetadataDetectedSpeaker)),
                        item.Metadata.Get(this.MetadataProduced));
                }
            }
        }

        private string CleanName(string name)
        {
            if (name == null)
                return null;
            var index = name.IndexOf('(');
            return (index < 0 ? name : name.Substring(0, index)).Trim();
        }
    }
}