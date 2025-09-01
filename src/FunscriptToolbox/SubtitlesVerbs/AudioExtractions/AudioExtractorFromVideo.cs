using Newtonsoft.Json;
using System.IO;

namespace FunscriptToolbox.SubtitlesVerbs.AudioExtractions
{
    public class AudioExtractorFromVideo : AudioExtractor
    {
        [JsonProperty(Order = 10)]
        public string FfmpegParameters { get; set; } = "";

        [JsonProperty(Order = 11)]
        public string SaveAsFileSuffixe { get; set; }

        protected override bool IsPrerequisitesMet(SubtitleGeneratorContext context, out string reason)
        {
            reason = null;
            return true;
        }

        protected override void ExtractAudio(
            SubtitleGeneratorContext context, 
            AudioExtraction audioExtraction)
        {
            var videoPath = context.WIP.OriginalVideoPath;
            context.WriteInfo($"   Extracting PCM audio from '{Path.GetFileName(videoPath)}'...");

            var audio = context.FfmpegAudioHelper.ExtractPcmAudio(
                videoPath,
                this.FfmpegParameters);

            audioExtraction.SetPcmAudio(context, audio);
            if (this.SaveAsFileSuffixe != null)
            {
                var saveAsPath = context.WIP.BaseFilePath + this.SaveAsFileSuffixe;
                context.FfmpegAudioHelper.ConvertPcmAudioToOtherFormat(audio, saveAsPath);
            }

            context.WIP.Save();            
        }
    }
}