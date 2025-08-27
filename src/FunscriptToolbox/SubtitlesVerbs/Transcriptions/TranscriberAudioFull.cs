using Newtonsoft.Json;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscriberAudioFull : TranscriberAudio
    {
        public TranscriberAudioFull()
        {
        }

        [JsonProperty(Order = 30, Required = Required.Always)]
        public TranscriberAudioTool TranscriberTool { get; set; }

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
                     new[] { context.CurrentWipsub.PcmAudio });
            transcription.Items.AddRange(transcribedTexts);
            transcription.MarkAsFinished();
            context.CurrentWipsub.Save();

            SaveDebugSrtIfVerbose(context, transcription);
        }
    }
}