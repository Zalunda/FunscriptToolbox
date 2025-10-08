using FunscriptToolbox.Core;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Outputs
{
    internal class SubtitleOutputSimpleSrt : SubtitleOutput
    {
        public SubtitleOutputSimpleSrt()
        {

        }


        [JsonProperty(Order = 5, Required = Required.Always)]
        public string FileSuffix { get; set; }
        [JsonProperty(Order = 10)]
        public string WorkerId { get; set; }
        [JsonProperty(Order = 11)]
        public string MetadataToUse { get; set; }
        [JsonProperty(Order = 12)]
        public TimeSpan MinimumSubtitleDuration { get; set; } = TimeSpan.Zero;
        [JsonProperty(Order = 13)]
        public TimeSpan ExpandSubtileDuration { get; set; } = TimeSpan.Zero;

        [JsonProperty(Order = 14)]
        public bool SaveFullFileToo = false;

        [JsonProperty(Order = 14)]
        public string AddToFirstSubtitle = string.Empty;

        [JsonProperty(Order = 20)]
        public SubtitleToInject[] SubtitlesToInject { get; set; }

        protected override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            reason = $"Cannot create file because transcription/translation '{this.WorkerId}' doesn't exists yet.";
            return (this.WorkerId == null || context.WIP.WorkersResult.Any(t => t.Id == this.WorkerId && t.IsFinished));
        }

        protected override bool IsFinished(SubtitleGeneratorContext context)
        {
            if (context.WIP.LoadVirtualSubtitleFile(this.FileSuffix) == null)
                return false;
            return (this.SaveFullFileToo && context.WIP.TimelineMap.Segments.Length > 1)
                ? File.Exists(Path.Combine(context.WIP.BaseFilePath + this.FileSuffix))
                : true;
        }

        protected override void DoWork(
            SubtitleGeneratorContext context)
        {
            var virtualSubtitleFile = context.WIP.CreateVirtualSubtitleFile();
            if (this.WorkerId == null)
            {
                if (this.AddToFirstSubtitle != null)
                {
                    virtualSubtitleFile.Subtitles.Add(
                        new Subtitle(
                            TimeSpan.Zero,
                            TimeSpan.FromSeconds(5),
                            this.AddToFirstSubtitle));                        
                }
            }
            else
            {
                var container = context.WIP.WorkersResult.FirstOrDefault(t => t.Id == this.WorkerId);

                var addToNextSubtitle = this.AddToFirstSubtitle == null ? null : "\n" + this.AddToFirstSubtitle;
                foreach (var item in container.GetItems())
                {
                    virtualSubtitleFile.Subtitles.Add(
                        new Subtitle(item.StartTime,
                        item.EndTime,
                        item.Metadata.Get(this.MetadataToUse ?? container.MetadataAlwaysProduced ?? string.Empty) + (addToNextSubtitle ?? string.Empty)));
                    addToNextSubtitle = null;
                }

                // Apply minimum duration and expansion
                virtualSubtitleFile.ExpandTiming(this.MinimumSubtitleDuration, this.ExpandSubtileDuration);

                if (this.SaveFullFileToo && context.WIP.TimelineMap.Segments.Length > 1)
                {
                    var newFullFile = new SubtitleFile();
                    newFullFile.Subtitles.AddRange(
                        container.GetItems().Select(item => 
                            new Subtitle(item.StartTime,
                                item.EndTime,
                                item.Metadata.Get(this.MetadataToUse ?? container.MetadataAlwaysProduced ?? string.Empty) + (addToNextSubtitle ?? string.Empty))));
                    VirtualSubtitleFile.InjectSubtitleInFile(newFullFile, this.SubtitlesToInject, TimeSpan.Zero);
                    newFullFile.ExpandTiming(this.MinimumSubtitleDuration, this.ExpandSubtileDuration);
                    newFullFile.SaveSrt(
                        Path.Combine(
                            context.WIP.BaseFilePath + this.FileSuffix));

                }
            }

            virtualSubtitleFile.Save(
                context.WIP.ParentPath,
                this.FileSuffix,
                context.SoftDelete,
                this.SubtitlesToInject);
        }
    }
}