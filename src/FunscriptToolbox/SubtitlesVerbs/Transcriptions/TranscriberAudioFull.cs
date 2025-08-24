using FunscriptToolbox.Core;
using FunscriptToolbox.SubtitlesVerbs.AudioExtraction;
using Newtonsoft.Json;
using System.Linq;
using System;

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
            if (Metadatas?.IsPrerequisitesMet(context, out reason) == false)
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
            var pcmAudio = context.CurrentWipsub.PcmAudio;
            this.TranscriberTool.TranscribeAudio(
                     context,
                     transcription,
                     new[] { new TimedObjectWithMetadata<PcmAudio>(pcmAudio.StartTime, pcmAudio.EndTime) { Tag = pcmAudio } });
            if (transcription.Items.Count > 0)
            {
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