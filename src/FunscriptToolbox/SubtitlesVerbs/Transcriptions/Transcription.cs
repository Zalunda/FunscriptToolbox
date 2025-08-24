using FunscriptToolbox.SubtitlesVerbs.Translations;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class Transcription : ITimedObjectWithMetadataCollection
    {
        public string Id { get; private set; }
        public Language Language { get; }
        public bool IsFinished { get; private set; }
        public List<TranscribedText> Items { get; }
        public List<TranscriptionCost> Costs { get; }
        public List<Translation> Translations { get; }

        ICollection<TimedObjectWithMetadata> ITimedObjectWithMetadataCollection.Items => this.Items.Cast<TimedObjectWithMetadata>().ToArray();

        public ITiming[] GetTimings() => this.Items.Cast<ITiming>().ToArray();

        public Transcription(
            string id,
            Language language,
            bool isFinished = false,
            IEnumerable<TranscribedText> items = null, 
            IEnumerable<TranscriptionCost> costs = null,
            IEnumerable<Translation> translations = null)
        {
            Id = id;
            Language = language;
            IsFinished = isFinished;
            Items = new List<TranscribedText>(items ?? Array.Empty<TranscribedText>());
            Costs = new List<TranscriptionCost>(costs ?? Array.Empty<TranscriptionCost>());
            Translations = new List<Translation>(translations ?? Array.Empty<Translation>());
        }

        public void Rename(string newId)
        {
            this.Id = newId;
        }

        public void MarkAsFinished()
        {
            this.Items.Sort((a, b) => (int)(a.StartTime.TotalMilliseconds - b.StartTime.TotalMilliseconds));
            this.IsFinished = true;
        }

        public TranscriptionAnalysis<T> GetAnalysis<T>(
            T[] timings) where T : class, ITiming
        {
            return (timings == null)
                    ? null :
                    TranscriptionAnalysis<T>.From(
                        this,
                        timings);
        }
    }
}