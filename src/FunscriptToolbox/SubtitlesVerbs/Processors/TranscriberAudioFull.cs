using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Processors
{
    public class TranscriberAudioFull : TranscriberAudio
    {
        [JsonProperty(Order = 10, Required = Required.Always)]
        public string MetadataProduced { get; set; }

        [JsonProperty(Order = 20, Required = Required.Always)]
        public TranscriberToolAudio TranscriberTool { get; set; }

        protected override string GetMetadataProduced() => this.MetadataProduced;

        protected override void DescribeDataFlow(Infra.WorkerDataFlow flow)
        {
            base.DescribeDataFlow(flow);
            if (this.TranscriberTool is TranscriberToolAudioExternal external)
            {
                // The external tool: a .wav is written, a .srt is read
                flow.Output(Infra.DataKind.File, $".TODO-{this.Id}_*.wav", "for the external tool");
                flow.Input(Infra.DataKind.File, external.OverrideFileSuffixe ?? $".TODO-{this.Id}_*.srt", "from the external tool");
            }
        }

        protected override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            if (!base.IsPrerequisitesForAudioMet(context, out reason))
            {
                return false;
            }
            reason = null;
            return true;
        }

        protected override void DoWorkInternal(SubtitleGeneratorContext context, Track transcription)
        { 
            var transcribedTexts = this.TranscriberTool.TranscribeAudio(
                     context,
                     transcription,
                     new[] { base.GetPcmAudio(context) },
                     this.MetadataProduced);
            transcription.Items.AddRange(transcribedTexts);
            transcription.MarkAsFinished();
            context.WIP.Save();
        }
    }
}