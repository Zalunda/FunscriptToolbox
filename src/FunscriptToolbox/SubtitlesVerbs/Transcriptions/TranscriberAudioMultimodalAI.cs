using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscriberAudioMultimodalAI : TranscriberAudio
    {
        public TranscriberAudioMultimodalAI()
        {
        }

        [JsonProperty(Order = 20)]
        public TimeSpan ExpandStart { get; set; } = TimeSpan.Zero;
        [JsonProperty(Order = 21)]
        public TimeSpan ExpandEnd { get; set; } = TimeSpan.Zero;
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
            if (Metadatas?.Aggregate(context).IsPrerequisitesMetWithTimings(out reason) == false)
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
            var itemsWithAudios = itemsToDo.Union(itemsForTraining).Distinct().ToArray();
            var index = 1;
            var binaryContentsDictionary = new Dictionary<TimeSpan, dynamic[]>();
            foreach (var item in itemsWithAudios)
            {
                context.DefaultProgressUpdateHandler("ffmpeg", $"{index++}/{itemsWithAudios.Length}", $"Generating .wav for {item.StartTime} to {item.EndTime}");
                var tempWavFile = Path.GetTempFileName() + ".wav";
                context.FfmpegAudioHelper.ConvertPcmAudioToWavFile(
                    context.CurrentWipsub.PcmAudio.ExtractSnippet(item.StartTime - this.ExpandStart, item.EndTime + this.ExpandEnd), tempWavFile);

                var audioBytes = File.ReadAllBytes(tempWavFile);
                var base64Audio = Convert.ToBase64String(audioBytes);
                File.Delete(tempWavFile);
                binaryContentsDictionary.Add(
                    item.StartTime,
                    new[]
                    {
                        new
                        {
                            type = "input_audio",
                            input_audio = new
                            {
                                data = base64Audio,
                                format = "wav"
                            }
                        }
                    });
                if (this.KeepTemporaryFiles)
                    context.CreateVerboseBinaryFile($"{transcription.Id}_{item.StartTime:hhmmssfff}.wav", audioBytes, processStartTime);
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