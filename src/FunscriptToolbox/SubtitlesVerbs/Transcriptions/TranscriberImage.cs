using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscriberImage : TranscriberAudio
    {
        public TranscriberImage()
        {
        }


        [JsonProperty(Order = 21)]
        internal string FfmpegFilter { get; set; }
        [JsonProperty(Order = 22)]
        public bool KeepTemporaryFiles { get; set; } = false;

        [JsonProperty(Order = 30, Required = Required.Always)]
        public AIEngine Engine { get; set; }
        [JsonProperty(Order = 31, Required = Required.Always)]
        internal MetadataAggregator Metadatas { get; set; }
        [JsonProperty(Order = 32)]
        public AIOptions Options { get; set; } = new AIOptions();


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
                .Aggregate(context, mergeRules: this.Options?.MergeRules)
                .CreateRequestGenerator(transcription, this.Options);
            var runner = new AIEngineRunner<TranscribedItem>(
                context,
                this.Engine,
                transcription);

            var (itemsToDo, _, _, itemsForTraining) = requestGenerator.AnalyzeItemsState();
            var index = 1;
            var itemsWithImages = itemsToDo.Union(itemsForTraining).Distinct().ToArray();
            var binaryContentsDictionary = new Dictionary<TimeSpan, dynamic[]>();
            foreach (var item in itemsWithImages)
            {
                var middleTime = TimeSpan.FromMilliseconds((item.StartTime.TotalMilliseconds + item.EndTime.TotalMilliseconds) / 2);
                context.DefaultProgressUpdateHandler("ffmpeg", $"{index++}/{itemsWithImages.Length}", $"Taking screenshot of {middleTime}");
                var image = context.FfmpegAudioHelper.TakeScreenshotAsBytes(
                    context.CurrentWipsub.OriginalVideoPath,
                    middleTime,
                    ".jpg",
                    this.FfmpegFilter);
                binaryContentsDictionary.Add(
                    item.StartTime,
                    new[] 
                    { 
                        new
                        {
                            type = "image_url",
                            image_url = new
                            {
                                url = $"data:image/jpeg;base64,{Convert.ToBase64String(image)}"
                            }
                        } 
                    });
                if (KeepTemporaryFiles)
                    context.CreateVerboseBinaryFile($"{transcription.Id}_{middleTime:hhmmssfff}.jpg", image, processStartTime);
            }
            context.ClearProgressUpdate();

            runner.Run(requestGenerator, binaryContentsDictionary);

            if (requestGenerator.IsFinished())
            {
                transcription.MarkAsFinished();
                context.CurrentWipsub.Save();
            }

            SaveDebugSrtIfVerbose(context, transcription);
        }
    }
}