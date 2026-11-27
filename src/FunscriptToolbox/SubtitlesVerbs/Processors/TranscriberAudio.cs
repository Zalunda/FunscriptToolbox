using FunscriptToolbox.SubtitlesVerbs.Inputs;
using Newtonsoft.Json;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Processors
{
    public abstract class TranscriberAudio : Transcriber 
    {
        [JsonProperty(Order = 5, Required = Required.Always)]
        public string SourceAudioId { get; internal set; }

        protected override void DescribeDataFlow(Infra.WorkerDataFlow flow)
        {
            base.DescribeDataFlow(flow);
            flow.Input(Infra.DataKind.Audio, this.SourceAudioId, "audio");
        }

        internal bool IsPrerequisitesForAudioMet(SubtitleGeneratorContext context, out string reason)
        {
            if (GetPcmAudio(context) == null)
            {
                reason = $"Audio extraction '{this.SourceAudioId}' is not done yet.";
                return false;
            }

            reason = null;
            return true;
        }

        protected PcmAudio GetPcmAudio(SubtitleGeneratorContext context)
        {
            return context.WIP.AudioExtractions.FirstOrDefault(f => f.Id == SourceAudioId && f.IsFinished)?.PcmAudio;
        }
    }
}