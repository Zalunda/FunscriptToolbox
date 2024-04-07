using FunscriptToolbox.SubtitlesVerbV2;
using System;
using Newtonsoft.Json;

namespace FunscriptToolbox.SubtitlesVerbsV2.Outputs
{
    internal class SubtitleOutputWav : SubtitleOutput
    {
        public SubtitleOutputWav()
        {
            
        }

        public override bool NeedSubtitleForcedTimings => true;

        [JsonProperty(Order = 10)]
        public string FileSuffixe { get; set; } = ".wav";

        public override void CreateOutput(
            SubtitleGeneratorContext context,
            WorkInProgressSubtitles wipsub)
        {
            if (this.FileSuffixe == null)
            {
                throw new ArgumentNullException($"{typeof(SubtitleOutputWIPSrt).Name}.FileSuffixe");
            }

            var filename = context.BaseFilePath + this.FileSuffixe;
            context.SoftDelete(filename);
            context.FfmpegAudioHelper.ConvertPcmAudioToWavFile(context.Wipsub.PcmAudio, filename);
        }
    }
}
