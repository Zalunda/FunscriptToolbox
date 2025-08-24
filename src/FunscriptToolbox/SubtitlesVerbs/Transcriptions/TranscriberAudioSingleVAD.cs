using FunscriptToolbox.Core;
using FunscriptToolbox.SubtitlesVerbs.AudioExtraction;
using Newtonsoft.Json;
using System;
using System.Linq;

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

        public override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            if (Metadatas?.IsPrerequisitesMetIncludingTimings(context, out reason) == false)
            {
                return false;
            }

            reason = null; 
            return true;
        }

        public override void Transcribe(
            SubtitleGeneratorContext context,
            Transcription transcription)
        {
            var items = this.Metadatas.GetTimingsWithMetadata<PcmAudio>(context, transcription);

            foreach (var item in items.Where(t => t.Metadata.IsVoice).ToArray())
            {
                item.Tag = context.CurrentWipsub.PcmAudio.ExtractSnippet(item.StartTime - this.ExpandStart, item.EndTime + this.ExpandEnd);
            }
            this.TranscriberTool.TranscribeAudio(
                context,
                transcription,
                items);
            if (!transcription.Items.Any(item => item.Metadata.IsVoice && item.Metadata.VoiceText == null) 
                && !items.Any(f => f.Metadata.IsVoice && !transcription.Items.Any(k => k.StartTime == f.StartTime)))
            {
                // TODO Test
                transcription.MarkAsFinished();
            }

            // Save verbose output if needed
            if (context.IsVerbose)
            {
                var srt = new SubtitleFile();
                srt.Subtitles.AddRange(transcription.Items.Select(item =>
                    new Subtitle(
                        item.StartTime,
                        item.EndTime,
                        item.Text + "\n" + string.Join("\n", item.Metadata.Select(kvp => $"{{{kvp.Key}:{kvp.Value}}}")))));
                srt.SaveSrt(context.GetPotentialVerboseFilePath($"{transcription.Id}.srt", DateTime.Now));
            }
        }
    }
}