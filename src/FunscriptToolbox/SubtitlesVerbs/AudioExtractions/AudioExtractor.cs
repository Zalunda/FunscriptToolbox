using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.AudioExtractions
{
    public abstract class AudioExtractor : SubtitleWorker
    {
        [JsonProperty(Order = 1, Required = Required.Always)]
        public string AudioExtractionId { get; set; }

        protected override string GetId() => this.AudioExtractionId;
        protected override string GetWorkerTypeName() => "Audio extraction";
        protected override string GetExecutionVerb() => "Extracting audio";

        protected override bool IsFinished(SubtitleGeneratorContext context)
        {
            var audioExtraction = context.WIP.AudioExtractions.FirstOrDefault(t => t.Id == this.AudioExtractionId);
            return audioExtraction != null && audioExtraction.IsFinished;
        }

        protected override void EnsureDataObjectExists(SubtitleGeneratorContext context)
        {
            if (!context.WIP.AudioExtractions.Any(t => t.Id == this.AudioExtractionId))
            {
                var audioExtraction = new AudioExtraction(this.AudioExtractionId);
                context.WIP.AudioExtractions.Add(audioExtraction);
            }
        }

        protected override IEnumerable<string> GetAdditionalStatusLines(SubtitleGeneratorContext context)
        {
            var audioExtraction = context.WIP.AudioExtractions.First(t => t.Id == this.AudioExtractionId);
            yield return $"Audio Duration = {audioExtraction.PcmAudio.Duration}";
        }
    }
}