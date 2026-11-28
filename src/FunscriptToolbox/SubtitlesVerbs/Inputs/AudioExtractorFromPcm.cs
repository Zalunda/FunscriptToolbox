using FunscriptToolbox.Core.Infra;
using Newtonsoft.Json;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Inputs
{
    [ConfigDoc("Another version of an audio extraction (SourceAudioId), through ffmpeg filters (FfmpegParameters), ex. a band-pass and a normalization: a cleaner waveform for the timing refiner. SaveAsFileSuffixe: also saved as a file next to each part of the scene.")]
    public class AudioExtractorFromPcm : AudioExtractor
    {
        [ConfigDoc("The Id of the audio extraction it starts from (ex. \"audio\").")]
        [JsonProperty(Order = 10)]
        public string SourceAudioId { get; set; }

        [ConfigDoc("The ffmpeg parameters applied to that audio (ex. a filter: \"-af \\\"highpass=f=300,lowpass=f=3500,loudnorm=I=-16:TP=-1\\\"\"). The sampling rate stays the same.")]
        [JsonProperty(Order = 11)]
        public string FfmpegParameters { get; set; } = "";

        [ConfigDoc("Also saved as a file next to each part of the scene, with this suffix (ex. \".wav\": the extension decides the format). null: not saved.")]
        [JsonProperty(Order = 12)]
        public string SaveAsFileSuffixe { get; set; }

        protected override void DescribeDataFlow(Infra.WorkerDataFlow flow)
        {
            base.DescribeDataFlow(flow);
            flow.Input(Infra.DataKind.Audio, this.SourceAudioId, "audio");
            flow.Output(Infra.DataKind.File, this.SaveAsFileSuffixe);
        }

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

        protected override void DoWork(
            SubtitleGeneratorContext context)
        {
            var audioExtraction = context.WIP.AudioExtractions.First(t => t.Id == this.Id);

            context.WriteInfo($"   Transforming PCM audio from '{this.SourceAudioId}'...");

            var tempPcmFile = PathExtension.GetTempFileName(".pcm");
            try
            {
                var originalPcmAudio = GetPcmAudio(context);

                var newPcmAudio = context.RunStep($"Transform the audio of '{this.SourceAudioId}'", _ =>
                {
                    context.FfmpegHelper.ConvertPcmAudioToOtherFormat(originalPcmAudio, tempPcmFile, $"-f s16le -ar {originalPcmAudio.SamplingRate} " + this.FfmpegParameters);
                    return new PcmAudio(originalPcmAudio.SamplingRate, File.ReadAllBytes(tempPcmFile));
                });

                audioExtraction.SetPcmAudio(context, newPcmAudio);
                if (this.SaveAsFileSuffixe != null)
                {
                    // Need to resplit using TimelineMap
                    foreach (var segment in context.WIP.TimelineMap.Segments)
                    {
                        var saveAsFilename = Path.ChangeExtension(segment.Filename, this.SaveAsFileSuffixe);
                        context.RunStep($"Save the audio as '{saveAsFilename}'", _ =>
                            context.FfmpegHelper.ConvertPcmAudioToOtherFormat(
                                newPcmAudio.ExtractSnippet(
                                    segment.Offset,
                                    segment.Offset + segment.Duration),
                                Path.Combine(
                                    context.WIP.ParentPath,
                                    saveAsFilename)));
                    }
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