using Newtonsoft.Json;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscriberAudioFull : Transcriber
    {
        public TranscriberAudioFull()
        {
        }

        [JsonProperty(Order = 10, Required = Required.Always)]
        public string MetadataProduced { get; set; }

        [JsonProperty(Order = 20, Required = Required.Always)]
        public TranscriberToolAudio TranscriberTool { get; set; }

        protected override string GetMetadataProduced() => this.MetadataProduced;

        protected override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
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
                     new[] { context.CurrentWipsub.PcmAudio },
                     this.MetadataProduced);
            transcription.Items.AddRange(transcribedTexts);
            transcription.MarkAsFinished();
            context.CurrentWipsub.Save();

            SaveDebugSrtIfVerbose(context, transcription);
        }
    }
}