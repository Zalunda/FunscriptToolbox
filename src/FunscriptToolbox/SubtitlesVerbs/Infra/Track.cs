using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    /// <summary>
    /// What a step produces (see TrackProducer): timed items with metadata, kept in the .wipsubtitles under the id of the step.
    /// (before the format 3.0: a 'Transcription' or a 'Translation', in 2 lists: see WorkInProgressSubtitles)
    /// </summary>
    public class Track : TimedItemWithMetadataCollection<TimedItemWithMetadata>
    {
        /// <summary>The language of the text produced (ex. the language of the video for a transcription, the target language for a translation).</summary>
        [JsonProperty(Order = 10)]
        public Language Language { get; }

        [JsonProperty(Order = 11)]
        public bool HasStoppedOnFirstExecution { get; set; } = false;

        /// <summary>
        /// What the step keeps between its runs (ex. the answers of the AI not used yet, the review of the user). Saved with its type
        /// ('$type'); a job state that can't be read anymore (ex. its class changed since) is read as null, see TolerantJobStateConverter.
        /// </summary>
        [JsonProperty(Order = 12, TypeNameHandling = TypeNameHandling.Auto)]
        [JsonConverter(typeof(TolerantJobStateConverter))]
        public object CurrentJobState { get; set; }

        public Track(
            string id,
            string metadataAlwaysProduced,
            string[] privateMetadataNames,
            Language language,
            bool isFinished = false,
            IEnumerable<TimedItemWithMetadata> items = null,
            IEnumerable<Cost> costs = null,
            object currentJobState = null)
            : base(id, metadataAlwaysProduced, privateMetadataNames, isFinished, items, costs)
        {
            Language = language;
            CurrentJobState = currentJobState;
            IsFinished = isFinished;
        }

        public override TimedItemWithMetadata AddNewItem(
            TimeSpan startTime,
            TimeSpan endTime,
            MetadataCollection extraMetadatas)
        {
            var newItem = new TimedItemWithMetadata(startTime, endTime, extraMetadatas);
            this.Items.Add(newItem);
            return newItem;
        }

        public void MarkAsFinished()
        {
            this.Items.Sort((a, b) => (int)(a.StartTime.TotalMilliseconds - b.StartTime.TotalMilliseconds));
            this.IsFinished = true;
        }

        public void MarkAsNotFinished()
        {
            this.IsFinished = false;
        }
    }
}
