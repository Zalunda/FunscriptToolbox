using Newtonsoft.Json;
using FunscriptToolbox.SubtitlesVerbs.Infra;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscriberAudioFull : TranscriberAudio
    {
        public TranscriberAudioFull()
        {
        }

        [JsonProperty(Order = 20)]
        public MetadataAggregator Metadatas { get; set; }

        [JsonProperty(Order = 30, Required = Required.Always)]
        public TranscriberAudioTool TranscriberTool { get; set; }

        public override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            reason = null;
            return true;
        }

        public override void Transcribe(
            SubtitleGeneratorContext context,
            Transcription transcription)
        {
            var transcribedTexts = this.TranscriberTool.TranscribeAudio(
                     context,
                     transcription,
                     new[] { context.CurrentWipsub.PcmAudio });
            transcription.Items.AddRange(transcribedTexts);
            transcription.MarkAsFinished();

            SaveDebugSrtIfVerbose(context, transcription);
        }
    }
}