using Newtonsoft.Json;
using System.IO;

namespace FunscriptToolbox.SubtitlesVerbs.Outputs
{
    internal class SubtitleOutputWav : SubtitleOutput
    {
        public SubtitleOutputWav()
        {
            
        }

        [JsonProperty(Order = 10, Required = Required.Always)]
        public string FileSuffix { get; set; }

        [JsonProperty(Order = 11)]
        public string FfmpegWavParameters { get; set; } = string.Empty;

        public override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            reason = null;
            return true;
        }

        public override void CreateOutput(
            SubtitleGeneratorContext context)        
        {
            var filename = context.CurrentBaseFilePath + this.FileSuffix;
            if (File.Exists(filename))
            {
                context.WriteInfoAlreadyDone($"Output '{Path.GetFileName(filename)}' already exists.");
            }
            else
            {
                context.SoftDelete(filename);
                context.FfmpegAudioHelper.ConvertPcmAudioToWavFile(context.CurrentWipsub.PcmAudio, filename, this.FfmpegWavParameters);
            }
        }
    }
}
