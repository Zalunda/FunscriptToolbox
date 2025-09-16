using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System.Collections.Generic;
using System;
using System.IO;
using System.Linq;

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

        protected override void DoWork(
            SubtitleGeneratorContext context)
        {
            var audioExtraction = context.WIP.AudioExtractions.First(t => t.Id == this.AudioExtractionId);
            var mergedPcmStream = new MemoryStream();

            // The initial TimelineMap only has filenames. We now populate the durations, while accumulating the PcmAudioData
            var currentOffset = TimeSpan.Zero;
            var newSegments = new List<TimelineSegment>();
            foreach (var segment in context.WIP.TimelineMap.Segments)
            {
                string fullPartPath = Path.Combine(context.WIP.ParentPath, segment.Filename);

                context.WriteInfo($"   Extracting PCM audio from '{Path.GetFileName(fullPartPath)}'...");
                PcmAudio partPcm = context.FfmpegHelper.ExtractPcmAudio(fullPartPath, this.FfmpegParameters);
                mergedPcmStream.Write(partPcm.Data, 0, partPcm.Data.Length);

                // Add the updated segment with its real duration to our new list.
                newSegments.Add(new TimelineSegment(segment.Filename, partPcm.Duration, currentOffset));
                currentOffset += partPcm.Duration;

                if (this.SaveAsFileSuffixe != null)
                {
                    var saveAsPath = Path.ChangeExtension(fullPartPath, this.SaveAsFileSuffixe);
                    context.FfmpegHelper.ConvertPcmAudioToOtherFormat(partPcm, saveAsPath);
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