using FunscriptToolbox.SubtitlesVerbs.AudioExtraction;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscriberAudioSingleVAD : TranscriberAudio
    {
        public TranscriberAudioSingleVAD()
        {
        }


        [JsonProperty(Order = 20, Required = Required.Always)]
        internal MetadataAggregator Metadatas { get; set; }
        [JsonProperty(Order = 21)]
        public TimeSpan ExpandStart { get; set; } = TimeSpan.Zero;
        [JsonProperty(Order = 22)]
        public TimeSpan ExpandEnd { get; set; } = TimeSpan.Zero;

        [JsonProperty(Order = 30)]
        public TranscriberAudioTool TranscriberTool { get; set; }

        protected override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            if (this.Metadatas.Aggregate(context).IsPrerequisitesMetWithTimings(out reason) == false)
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
            var timings = this.Metadatas
                .Aggregate(context)
                .CreateRequestGenerator(transcription)
                .GetTimings();

            var audios = new List<PcmAudio>();
            foreach (var timing in timings)
            {
                audios.Add(context.CurrentWipsub.PcmAudio.ExtractSnippet(timing.StartTime - this.ExpandStart, timing.EndTime + this.ExpandEnd));
            }

            var transcribedTexts = this.TranscriberTool.TranscribeAudio(
                context,
                transcription,
                audios.ToArray());

            transcription.Items.AddRange(transcribedTexts);
            transcription.MarkAsFinished();
            context.CurrentWipsub.Save();

            SaveDebugSrtIfVerbose(context, transcription);
        }
    }
}