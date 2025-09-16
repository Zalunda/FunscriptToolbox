using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;
using System.IO;
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

        protected override void DoWork(SubtitleGeneratorContext context)
        {
            var transcription = context.WIP.Transcriptions.FirstOrDefault(t => t.Id == this.TranscriptionId);
            var processStartTime = DateTime.Now;

            var requestGenerator = this.Metadatas
                .Aggregate(context)
                .CreateRequestGenerator(transcription, this.Options);
            var runner = new AIEngineRunner<TranscribedItem>(
                context,
                this.Engine,
                transcription);

            var binaryGenerator = new CachedBinaryGenerator("Image", (timing, text) =>
                    {
                        var middleTime = TimeSpan.FromMilliseconds((timing.StartTime.TotalMilliseconds + timing.EndTime.TotalMilliseconds) / 2);
                        context.DefaultProgressUpdateHandler("ffmpeg", $"{timing.StartTime}", $"Taking screenshot.");
                        var (filename, middleTimeInRightFile) = context.WIP.TimelineMap.GetPathAndPosition(middleTime);
                        var image = context.FfmpegHelper.TakeScreenshotAsBytes(
                            Path.GetFullPath(Path.Combine(context.WIP.ParentPath, filename)),
                            middleTimeInRightFile,
                            ".jpg",
                            this.FfmpegFilter?.Replace("[STARTTIME]", context.FfmpegHelper.EscapeFfmpegDrawtext(text)));
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
        }
    }
}