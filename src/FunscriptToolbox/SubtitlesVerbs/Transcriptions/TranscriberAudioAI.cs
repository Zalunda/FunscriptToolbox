using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;
using System.IO;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscriberAudioAI : TranscriberAudio
    {
        public TranscriberAudioAI()
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
                .CreateRequestGenerator(transcription, this.Options, transcription.Language);
            var runner = new AIEngineRunner<TranscribedItem>(
                context,
                this.Engine,
                transcription);

            var binaryGenerator = new CachedBinaryGenerator((timing) =>
            {
                context.DefaultProgressUpdateHandler("ffmpeg", $"{timing.StartTime}", $"Generating .wav for {timing.StartTime} to {timing.EndTime}");
                var tempWavFile = Path.GetTempFileName() + ".wav";
                context.FfmpegAudioHelper.ConvertPcmAudioToWavFile(
                    context.CurrentWipsub.PcmAudio.ExtractSnippet(timing.StartTime - this.ExpandStart, timing.EndTime + this.ExpandEnd), tempWavFile);

                var audioBytes = File.ReadAllBytes(tempWavFile);
                var base64Audio = Convert.ToBase64String(audioBytes);
                File.Delete(tempWavFile);
                var data = new[]
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
                    };
                if (this.KeepTemporaryFiles)
                    context.CreateVerboseBinaryFile($"{transcription.Id}_{timing.StartTime:hhmmssfff}.wav", audioBytes, processStartTime);
                return data;
            });

            runner.Run(requestGenerator, binaryGenerator);

            if (requestGenerator.IsFinished())
            {
                transcription.MarkAsFinished();
                context.CurrentWipsub.Save();
            }

            SaveDebugSrtIfVerbose(context, transcription);
        }
    }
}