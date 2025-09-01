using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.AudioExtractions
{
    public abstract class AudioExtractor : SubtitleWorker
    {
        [JsonProperty(Order = 1)]
        public bool Enabled { get; set; } = true;

        [JsonProperty(Order = 2, Required = Required.Always)]
        public string AudioExtractionId { get; set; }

        protected abstract bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason);

        protected abstract void ExtractAudio(
            SubtitleGeneratorContext context,
            AudioExtraction audioExtraction);

        public override void Execute(SubtitleGeneratorContext context)
        {
            if (!this.Enabled)
            {
                return;
            }

            var audioExtraction = context.WIP.AudioExtractions.FirstOrDefault(
                    t => t.Id == this.AudioExtractionId);
            if (audioExtraction == null)
            {
                audioExtraction = new AudioExtraction(
                    this.AudioExtractionId);
                context.WIP.AudioExtractions.Add(audioExtraction);
            }

            if (audioExtraction.IsFinished)
            {
                context.WriteInfoAlreadyDone($"Audio extraction '{this.AudioExtractionId}' has already been done:");
                context.WriteInfoAlreadyDone($"    Audio Duration = {audioExtraction.PcmAudio.Duration}");
                context.WriteInfoAlreadyDone();
            }
            else if (!this.IsPrerequisitesMet(context, out var reason))
            {
                context.WriteInfo($"Audio extraction '{this.AudioExtractionId}' can't be done yet: {reason}");
                context.WriteInfo();
            }
            else
            {
                try
                {
                    var watch = Stopwatch.StartNew();
                    context.WriteInfo($"Audio extraction '{this.AudioExtractionId}'...");
                    this.ExtractAudio(context, audioExtraction);

                    if (audioExtraction.IsFinished)
                    {
                        context.WriteInfo($"Finished in {watch.Elapsed}:");
                        context.WriteInfo($"    Audio Duration = {audioExtraction.PcmAudio.Duration}");
                    }
                    else
                    {
                        context.WriteInfo($"Not finished yet in {watch.Elapsed}.");
                    }
                    context.WriteInfo();
                }
                catch (Exception ex)
                {
                    context.WriteError($"An error occured while extracting audio '{this.AudioExtractionId}':\n{ex.Message}");
                    context.WriteLog(ex.ToString());
                }

            }
        }
    }
}