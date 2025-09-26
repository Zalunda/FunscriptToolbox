using FunscriptToolbox.SubtitlesVerbs.AudioExtractions;
using Newtonsoft.Json;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public abstract class TranscriberAudio : Transcriber 
    {
        [JsonProperty(Order = 5, Required = Required.Always)]
        public string SourceAudioId { get; internal set; }

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