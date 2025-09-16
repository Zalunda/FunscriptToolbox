using FunscriptToolbox.SubtitlesVerbs.AudioExtractions;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscriberAudioSingleVAD : TranscriberAudio
    {
        [JsonProperty(Order = 10, Required = Required.Always)]
        internal MetadataAggregator Metadatas { get; set; }
        [JsonProperty(Order = 11, Required = Required.Always)]
        public string MetadataProduced { get; set; }
        [JsonProperty(Order = 12)]
        public TimeSpan ExpandStart { get; set; } = TimeSpan.Zero;
        [JsonProperty(Order = 13)]
        public TimeSpan ExpandEnd { get; set; } = TimeSpan.Zero;

        [JsonProperty(Order = 30)]
        public TranscriberToolAudio TranscriberTool { get; set; }

        protected override string GetMetadataProduced() => this.MetadataProduced;

        protected override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            if (!base.IsPrerequisitesForAudioMet(context, out reason))
            {
                return false;
            }
            if (this.Metadatas.Aggregate(context).IsPrerequisitesMetWithTimings(out reason) == false)
            {
                return false;
            }

            reason = null;
            return true;
        }

        protected override void DoWork(SubtitleGeneratorContext context)
        {
            var transcription = context.WIP.Transcriptions.FirstOrDefault(t => t.Id == this.TranscriptionId);
            var timings = this.Metadatas
                .Aggregate(context)
                .CreateRequestGenerator(transcription)
                .GetTimings();

            var fullPcmAudio = base.GetPcmAudio(context);

            var audios = new List<PcmAudio>();
            foreach (var timing in timings)
            {
                audios.Add(fullPcmAudio.ExtractSnippet(timing.StartTime - this.ExpandStart, timing.EndTime + this.ExpandEnd));
            }

            var transcribedTexts = this.TranscriberTool.TranscribeAudio(
                context,
                transcription,
                audios.ToArray(),
                this.MetadataProduced);

            transcription.Items.AddRange(transcribedTexts);
            transcription.MarkAsFinished();
            context.WIP.Save();
        }
    }
}