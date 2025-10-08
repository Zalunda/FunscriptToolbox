using AudioSynchronization;
using FunscriptToolbox.Core;
using FunscriptToolbox.SubtitlesVerbs.AudioExtractions;
using Newtonsoft.Json;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Outputs
{
    internal class SubtitleOutputAsig : SubtitleOutput
    {
        public SubtitleOutputAsig()
        {

        }


        [JsonProperty(Order = 5, Required = Required.Always)]
        public string FileSuffix { get; set; }
        [JsonProperty(Order = 10, Required = Required.Always)]
        public string SourceAudioId { get; set; }
        [JsonProperty(Order = 11)]
        public bool SaveFullFileToo = false;

        protected override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            if (!context.WIP.AudioExtractions.Any(f => f.Id == SourceAudioId && f.IsFinished))
            {
                reason = $"Audio extraction '{this.SourceAudioId}' is not done yet.";
                return false;
            }

            reason = null;
            return true;
        }

        protected override bool IsFinished(SubtitleGeneratorContext context)
        {
            if (this.SaveFullFileToo && !File.Exists(context.WIP.BaseFilePath + this.FileSuffix))
            {
                return false;
            }

            foreach (var segment in context.WIP.TimelineMap.Segments)
            {
                if (!File.Exists(Path.ChangeExtension(
                        Path.Combine(context.WIP.ParentPath, segment.Filename),
                        this.FileSuffix)))
                    return false;
            }

            return true;
        }

        protected override void DoWork(
            SubtitleGeneratorContext context)
        {
            void SaveAsigFile(string asigPath, PcmAudio audio)
            {
                var tempFile = Path.GetTempFileName() + ".wav";
                context.FfmpegHelper.ConvertPcmAudioToOtherFormat(audio, tempFile);
                var signature = AudioTracksAnalyzer.ExtractSignature(tempFile);
                File.Delete(tempFile);
                var asig = new Funscript
                {
                    AudioSignature = new FunscriptAudioSignature(signature.NbSamplesPerSecond, signature.CompressedSamples)
                };
                asig.Save(asigPath);
            }
            var audio = context.WIP.AudioExtractions.First(f => f.Id == SourceAudioId && f.IsFinished).PcmAudio;

            if (this.SaveFullFileToo && !File.Exists(Path.Combine(context.WIP.BaseFilePath + this.FileSuffix)))
            {
                SaveAsigFile(context.WIP.BaseFilePath + this.FileSuffix, audio);
            }
            foreach (var segment in context.WIP.TimelineMap.Segments)
            {
                SaveAsigFile(
                    Path.ChangeExtension(
                        Path.Combine(context.WIP.ParentPath, segment.Filename), 
                        this.FileSuffix),
                    audio.ExtractSnippet(segment.Offset, segment.Offset + segment.Duration));
            }
        }
    }
}