using Newtonsoft.Json;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.AudioExtractions
{
    public class AudioExtractorFromPcm : AudioExtractor
    {
        [JsonProperty(Order = 10)]
        public string SourceAudioId { get; set; }

        [JsonProperty(Order = 11)]
        public string FfmpegParameters { get; set; } = "";

        [JsonProperty(Order = 12)]
        public string SaveAsFileSuffixe { get; set; }

        protected override bool IsPrerequisitesMet(SubtitleGeneratorContext context, out string reason)
        {
            if (GetPcmAudio(context) == null)
            {
                reason = $"Audio extraction '{this.SourceAudioId}' is not done yet.";
                return false;
            }

            reason = null;
            return true;
        }

        protected PcmAudio GetPcmAudio(SubtitleGeneratorContext context)
        {
            return context.WIP.AudioExtractions.FirstOrDefault(f => f.Id == SourceAudioId)?.PcmAudio;
        }

        protected override void ExtractAudio(
            SubtitleGeneratorContext context, 
            AudioExtraction audioExtraction)
        {
            context.WriteInfo($"   Transforming PCM audio from '{this.SourceAudioId}'...");

            var tempPcmFile = Path.GetTempFileName() + ".pcm";
            try
            {
                var originalPcmAudio = GetPcmAudio(context);

                context.FfmpegAudioHelper.ConvertPcmAudioToOtherFormat(originalPcmAudio, tempPcmFile, $"-f s16le -ar {originalPcmAudio.SamplingRate} " + this.FfmpegParameters);
               var newPcmAudio = new PcmAudio(originalPcmAudio.SamplingRate, File.ReadAllBytes(tempPcmFile));

                audioExtraction.SetPcmAudio(context, newPcmAudio);
                if (this.SaveAsFileSuffixe != null)
                {
                    var saveAsPath = context.WIP.BaseFilePath + this.SaveAsFileSuffixe;
                    context.FfmpegAudioHelper.ConvertPcmAudioToOtherFormat(newPcmAudio, saveAsPath);
                }

                context.WIP.Save();
            }
            finally
            {
                File.Delete(tempPcmFile);
            }
        }
    }
}