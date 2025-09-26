using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscriberAudioSingleVADAI : TranscriberAudio
    {
        [JsonProperty(Order = 20)]
        public TimeSpan ExpandStart { get; set; } = TimeSpan.Zero;
        [JsonProperty(Order = 21)]
        public TimeSpan ExpandEnd { get; set; } = TimeSpan.Zero;
        [JsonProperty(Order = 22)]
        public bool UpdateTimingsBeforeSaving { get; set; } = false;
        [JsonProperty(Order = 23)]
        public bool AddSpeechCadenceBeforeSaving { get; set; } = false;
        [JsonProperty(Order = 23)]
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
            if (!base.IsPrerequisitesForAudioMet(context, out reason))
            {
                return false;
            }
            if (Metadatas?.Aggregate(context).IsPrerequisitesMetWithTimings(out reason) == false)
            {
                return false;
            }

            reason = null;
            return true;
        }

        private static Regex r_leadingTrailingRegex = new Regex(@"^(\.\.(?<Leading>\d+|\?)\.\.)?(?<VoiceText>.*?)(\.\.(?<Trailing>\d+|\?)\.\.)?$", RegexOptions.Compiled);

        protected override void DoWork(SubtitleGeneratorContext context)
        {
            var transcription = context.WIP.Transcriptions.FirstOrDefault(t => t.Id == this.TranscriptionId);
            var processStartTime = DateTime.Now;

            var requestGenerator = this.Metadatas
                .Aggregate(context)
                .CreateRequestGenerator(transcription, this.Options, transcription.Language);
            var runner = new AIEngineRunner<TranscribedItem>(
                context,
                this.Engine,
                transcription);

            var fullPcmAudio = base.GetPcmAudio(context);

            var binaryGenerator = new CachedBinaryGenerator("Audio", (timing, _) =>
            {
                context.DefaultProgressUpdateHandler("ffmpeg", $"{timing.StartTime}", $"Generating .wav for {timing.StartTime} to {timing.EndTime}");
                var tempWavFile = Path.GetTempFileName() + ".wav";
                context.FfmpegHelper.ConvertPcmAudioToOtherFormat(
                    fullPcmAudio.ExtractSnippet(timing.StartTime - this.ExpandStart, timing.EndTime + this.ExpandEnd), tempWavFile);

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
                if (this.UpdateTimingsBeforeSaving || this.AddSpeechCadenceBeforeSaving)
                {
                    TimeSpan previousItemDesiredEndTime = TimeSpan.Zero;
                    for(int index = 0; index < transcription.Items.Count; index++)
                    {
                        var item = transcription.Items[index];
                        var previousItem = index > 0 ? transcription.Items[index - 1] : null;

                        var match = r_leadingTrailingRegex.Match(item.Metadata.Get(this.Options.MetadataAlwaysProduced));
                        if (match.Success)
                        {
                            var leadingDelay = TimeSpan.Zero;
                            if (match.Groups["Leading"].Success)
                            {
                                var leading = match.Groups["Leading"].Value;
                                if (leading != "?")
                                {
                                    leadingDelay = TimeSpan.FromSeconds(int.Parse(leading) * 0.1);
                                }
                                else
                                {
                                    leadingDelay = this.ExpandStart;
                                    item.Metadata["StartTimeTooLate"] = "true";
                                }
                            }
                            var trailingDelay = TimeSpan.Zero;
                            if (match.Groups["Trailing"].Success)
                            {
                                var trailing = match.Groups["Trailing"].Value;
                                if (trailing != "?")
                                {
                                    trailingDelay = TimeSpan.FromSeconds(int.Parse(trailing) * 0.1);
                                }
                                else
                                {
                                    trailingDelay = this.ExpandEnd;
                                    item.Metadata["EndTimeTooEarly"] = "true";
                                }
                            }

                            if (this.UpdateTimingsBeforeSaving)
                            {
                                item.StartTime = item.StartTime - this.ExpandStart + leadingDelay;
                                if (previousItem != null)
                                    previousItem.EndTime = (item.StartTime < previousItemDesiredEndTime) ? item.StartTime : previousItemDesiredEndTime;
                                previousItemDesiredEndTime = item.EndTime + this.ExpandEnd - trailingDelay;
                            }
                            if (this.AddSpeechCadenceBeforeSaving)
                            {
                                item.Metadata[this.Options.MetadataAlwaysProduced] = match.Groups["VoiceText"].Value;
                            }
                        }
                    }

                    if (this.UpdateTimingsBeforeSaving && transcription.Items.Count > 0)
                        transcription.Items.Last().EndTime = previousItemDesiredEndTime;
                }

                transcription.MarkAsFinished();
                context.WIP.Save();
            }
        }
    }
}