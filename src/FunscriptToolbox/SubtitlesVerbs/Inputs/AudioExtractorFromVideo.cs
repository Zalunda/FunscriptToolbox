using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System.Collections.Generic;
using System;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Inputs
{
    public class AudioExtractorFromVideo : AudioExtractor
    {
        [JsonProperty(Order = 10)]
        public string FfmpegParameters { get; set; } = "";

        [JsonProperty(Order = 11)]
        public string SaveAsFileSuffixe { get; set; }

        protected override void DescribeDataFlow(Infra.WorkerDataFlow flow)
        {
            base.DescribeDataFlow(flow);
            flow.Input(Infra.DataKind.Video, "video", "audio");
            flow.Output(Infra.DataKind.File, this.SaveAsFileSuffixe);
        }

        protected override bool IsPrerequisitesMet(SubtitleGeneratorContext context, out string reason)
        {
            reason = null;
            return true;
        }

        protected override void DoWork(
            SubtitleGeneratorContext context)
        {
            var audioExtraction = context.WIP.AudioExtractions.First(t => t.Id == this.Id);
            var mergedPcmStream = new MemoryStream();

            // The initial TimelineMap only has filenames. We now populate the durations, while accumulating the PcmAudioData
            var currentOffset = TimeSpan.Zero;
            var newSegments = new List<TimelineSegment>();
            var parts = new List<(string fullPartPath, PcmAudio pcm)>();

            // All the parts with the lock 'VID' kept: the parts of several scenes are not mixed (each scene gets all its audio before the next one)
            var segments = context.WIP.TimelineMap.Segments.ToArray();
            using (context.Locks.VideoSource.Acquire(segments.Length > 1
                ? $"ffmpeg: extracting the audio of the {segments.Length} parts"
                : "ffmpeg: extracting audio from the video"))
            {
                foreach (var segment in segments)
                {
                    string fullPartPath = Path.Combine(context.WIP.ParentPath, segment.Filename);

                    context.WriteInfo($"   Extracting PCM audio from '{Path.GetFileName(fullPartPath)}'...");
                    PcmAudio partPcm = context.RunStep(
                        $"Extract the audio from '{Path.GetFileName(fullPartPath)}'",
                        _ => context.FfmpegHelper.ExtractPcmAudio(fullPartPath, this.FfmpegParameters));
                    mergedPcmStream.Write(partPcm.Data, 0, partPcm.Data.Length);

                    // Add the updated segment with its real duration to our new list.
                    newSegments.Add(new TimelineSegment(segment.Filename, partPcm.Duration, currentOffset));
                    currentOffset += partPcm.Duration;
                    parts.Add((fullPartPath, partPcm));
                }
            }

            // (after: the lock 'VID' is not kept while waiting for the lock of the audio conversions)
            if (this.SaveAsFileSuffixe != null)
            {
                foreach (var (fullPartPath, partPcm) in parts)
                {
                    var saveAsPath = Path.ChangeExtension(fullPartPath, this.SaveAsFileSuffixe);
                    context.RunStep(
                        $"Save the audio as '{Path.GetFileName(saveAsPath)}'",
                        _ => context.FfmpegHelper.ConvertPcmAudioToOtherFormat(partPcm, saveAsPath));
                }
            }

            // Replace the old TimelineMap with the new one that includes durations.
            context.WIP.TimelineMap = new TimelineMap(newSegments.ToArray());

            var finalMergedPcm = new PcmAudio(16000, mergedPcmStream.ToArray());
            audioExtraction.SetPcmAudio(context, finalMergedPcm);

            context.WIP.Save();
        }
    }
}