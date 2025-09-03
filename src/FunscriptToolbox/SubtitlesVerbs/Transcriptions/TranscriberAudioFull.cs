using Newtonsoft.Json;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscriberAudioFull : TranscriberAudio
    {
        [JsonProperty(Order = 10, Required = Required.Always)]
        public string MetadataProduced { get; set; }

        [JsonProperty(Order = 20, Required = Required.Always)]
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
            reason = null;
            return true;
        }

        protected override void Transcribe(
            SubtitleGeneratorContext context,
            Transcription transcription)
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