using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.AudioExtractions
{
    public abstract class AudioExtractor : SubtitleWorker
    {
        protected override string GetDefaultProduces() => "Audio extraction";
        protected override string GetDefaultVerb() => "Extracting audio";

        protected override void DescribeDataFlow(WorkerDataFlow flow)
        {
            base.DescribeDataFlow(flow);
            flow.Output(DataKind.Audio, this.Id);
        }

        protected override bool IsFinished(SubtitleGeneratorContext context)
        {
            var audioExtraction = context.WIP.AudioExtractions.FirstOrDefault(t => t.Id == this.Id);
            return audioExtraction != null && audioExtraction.IsFinished;
        }

        protected override void EnsureDataObjectExists(SubtitleGeneratorContext context)
        {
            if (!context.WIP.AudioExtractions.Any(t => t.Id == this.Id))
            {
                var audioExtraction = new AudioExtraction(this.Id);
                context.WIP.AudioExtractions.Add(audioExtraction);
            }
        }

        protected override IEnumerable<string> GetAdditionalStatusLines(SubtitleGeneratorContext context)
        {
            var audioExtraction = context.WIP.AudioExtractions.First(t => t.Id == this.Id);
            yield return $"Audio Duration = {audioExtraction.PcmAudio.Duration}";
        }
    }
}