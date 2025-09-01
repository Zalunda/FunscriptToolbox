using FunscriptToolbox.Core;
using Newtonsoft.Json;
using System;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Outputs
{
    internal class SubtitleOutputSingleTranslationSrt : SubtitleOutput
    {
        public SubtitleOutputSingleTranslationSrt()
        {

        }

        public override string Description => $"{base.Description}: {this.FileSuffix}";

        [JsonProperty(Order = 10, Required = Required.Always)]
        public string FileSuffix { get; set; }
        [JsonProperty(Order = 11, Required = Required.Always)]
        public string WorkerId { get; set; }
        [JsonProperty(Order = 12)]
        public string MetadataToUse { get; set; }
        [JsonProperty(Order = 13)]
        public TimeSpan MinimumSubtitleDuration { get; set; } = TimeSpan.FromSeconds(1.5);
        [JsonProperty(Order = 14)]
        public TimeSpan ExpandSubtileDuration { get; set; } = TimeSpan.FromSeconds(0.5);

        [JsonProperty(Order = 16)]
        public string AddToFirstSubtitle = string.Empty;

        [JsonProperty(Order = 20)]
        public SubtitleToInject[] SubtitlesToInject { get; set; }

        public override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            reason = $"Cannot create file because transcription/translation '{this.WorkerId}' doesn't exists yet.";
            return (this.WorkerId == null) || (null != (context.CurrentWipsub.WorkersResult.FirstOrDefault(t => t.Id == this.WorkerId)));
        }

        public override void CreateOutput(
            SubtitleGeneratorContext context)
        {
            var container = context.CurrentWipsub.WorkersResult.FirstOrDefault(t => t.Id == this.WorkerId);

            var subtitleFile = new SubtitleFile();
            var addToNextSubtitle = this.AddToFirstSubtitle == null ? null : "\n" + this.AddToFirstSubtitle;
            foreach (var item in container.GetItems())
            {
                subtitleFile.Subtitles.Add(
                    new Subtitle(item.StartTime, 
                    item.EndTime, 
                    item.Metadata.Get(this.MetadataToUse ?? container.MetadataAlwaysProduced) + (addToNextSubtitle ?? string.Empty)));
                addToNextSubtitle = null;
            }

            // Apply minimum duration and expansion
            subtitleFile.ExpandTiming(this.MinimumSubtitleDuration, this.ExpandSubtileDuration);

            // Apply injections
            subtitleFile.Subtitles.AddRange(
                GetAdjustedSubtitlesToInject(
                    subtitleFile.Subtitles,
                    this.SubtitlesToInject,
                    context.CurrentWipsub.PcmAudio.Duration));

            var filename = context.CurrentBaseFilePath + this.FileSuffix;
            context.SoftDelete(filename);
            subtitleFile.SaveSrt(filename);
        }
    }
}