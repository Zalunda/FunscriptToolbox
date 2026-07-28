using Newtonsoft.Json;
using System.IO;

namespace FunscriptToolbox.SubtitlesVerbs.Outputs
{
    public class SubtitleOutputUsedConfig : SubtitleOutput
    {
        [JsonProperty(Order = 5, Required = Required.Always)]
        public string FileSuffix { get; set; } = ".used.config";
        [JsonProperty(Order = 6)]
        public bool RemoveUnusedTasks { get; set; } = true;
        [JsonProperty(Order = 7)]
        public bool RemoveUnusedSharedObjects { get; set; } = true;

        protected override bool IsPrerequisitesMet(SubtitleGeneratorContext context, out string reason)
        {
            reason = null;
            return true;
        }

        protected override bool IsFinished(SubtitleGeneratorContext context)
        {
            string targetPath = context.WIP.BaseFilePath + this.FileSuffix;
            return File.Exists(targetPath);
        }

        protected override void DoWork(SubtitleGeneratorContext context)
        {
            // Call the static method you just added to the Loader
            string finalContent = SubtitleGeneratorConfigLoader.GetMinimalFileContent(
                context.Config,
                this.RemoveUnusedTasks,
                this.RemoveUnusedSharedObjects);

            // Write it to disk
            string targetPath = context.WIP.BaseFilePath + this.FileSuffix;
            File.WriteAllText(targetPath, finalContent);
        }
    }
}