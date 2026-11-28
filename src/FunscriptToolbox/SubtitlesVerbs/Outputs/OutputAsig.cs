using AudioSynchronization;
using FunscriptToolbox.Core;
using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.Inputs;
using Newtonsoft.Json;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Outputs
{
    [ConfigDoc("Writes the audio signature ('.asig') of each part of the scene, from an audio extraction (SourceAudioId); SaveFullFileToo: also one of the whole scene. Used by the audio synchronization of FunscriptToolbox (ex. to move a script to another version of the video).")]
    internal class OutputAsig : SubtitleOutput
    {
        public OutputAsig()
        {

        }


        [ConfigDoc("The suffix of the files written, next to each part of the scene (ex. \".asig\").")]
        [JsonProperty(Order = 5, Required = Required.Always)]
        public string FileSuffix { get; set; }
        [ConfigDoc("The Id of the audio extraction it uses (ex. \"audio\": the original audio, not a filtered one).")]
        [JsonProperty(Order = 10, Required = Required.Always)]
        public string SourceAudioId { get; set; }
        [ConfigDoc("Also one file for the whole scene (all its parts, one after the other), named after the scene.")]
        [JsonProperty(Order = 11)]
        public bool SaveFullFileToo { get; set; } = false;

        protected override void DescribeDataFlow(Infra.WorkerDataFlow flow)
        {
            base.DescribeDataFlow(flow);
            flow.Input(Infra.DataKind.Audio, this.SourceAudioId, "audio");
            flow.Output(Infra.DataKind.File, this.FileSuffix);
        }

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
                var tempFile = PathExtension.GetTempFileName(".wav");
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