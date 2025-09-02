using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscriberImageAI : Transcriber
    {
        [JsonProperty(Order = 21)]
        internal string FfmpegFilter { get; set; }
        [JsonProperty(Order = 22)]
        public bool KeepTemporaryFiles { get; set; } = false;

        [JsonProperty(Order = 30, Required = Required.Always)]
        public AIEngine Engine { get; set; }
        [JsonProperty(Order = 31, Required = Required.Always)]
        internal MetadataAggregator Metadatas { get; set; }
        [JsonProperty(Order = 32, Required = Required.Always)]
        public AIOptions Options { get; set; } = new AIOptions();

        protected override string GetMetadataProduced() => this.Options.MetadataAlwaysProduced;

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

            var requestGenerator = this.Metadatas
                .Aggregate(context)
                .CreateRequestGenerator(transcription, this.Options);
            var runner = new AIEngineRunner<TranscribedItem>(
                context,
                this.Engine,
                transcription);

            // TODO be able to take the screenshot in the highest percentage for speaker detection (ex. when she open mouth or something)
            var binaryGenerator = new CachedBinaryGenerator((timing) =>
                    {
                        var middleTime = TimeSpan.FromMilliseconds((timing.StartTime.TotalMilliseconds + timing.EndTime.TotalMilliseconds) / 2);
                        context.DefaultProgressUpdateHandler("ffmpeg", $"{timing.StartTime}", $"Taking screenshot.");
                        var image = context.FfmpegAudioHelper.TakeScreenshotAsBytes(
                            context.WIP.OriginalVideoPath,
                            middleTime,
                            ".jpg",
                            this.FfmpegFilter);
                        var data = new[]
                            {
                                new
                                {
                                    type = "image_url",
                                    image_url = new
                                    {
                                        url = $"data:image/jpeg;base64,{Convert.ToBase64String(image)}"
                                    }
                                }
                            };
                        if (KeepTemporaryFiles)
                            context.CreateVerboseBinaryFile($"{transcription.Id}_{middleTime:hhmmssfff}.jpg", image, processStartTime);
                        return data;
                    });

            runner.Run(requestGenerator, binaryGenerator);

            if (requestGenerator.IsFinished())
            {
                transcription.MarkAsFinished();
                context.WIP.Save();
            }

            SaveDebugSrtIfVerbose(context, transcription);
        }
    }
}