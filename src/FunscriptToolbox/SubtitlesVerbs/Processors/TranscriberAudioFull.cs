using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Processors
{
    [ConfigDoc("A transcription of the whole audio (SourceAudioId) by a tool (TranscriberTool), ex. Whisper on this computer: subtitles with their timings, in the language of the video. Free (no AI request), less precise than an AI transcription.")]
    public class TranscriberAudioFull : TranscriberAudio
    {
        [ConfigDoc("The metadata the text goes in (ex. \"VoiceText\").")]
        [JsonProperty(Order = 10, Required = Required.Always)]
        public string MetadataProduced { get; set; }

        [ConfigDoc("The tool that transcribes: TranscriberToolAudioPurfviewWhisper (Whisper on this computer), or TranscriberToolAudioExternal (you, with another tool).")]
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