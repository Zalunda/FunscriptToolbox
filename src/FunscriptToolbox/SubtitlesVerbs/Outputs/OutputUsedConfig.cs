using Newtonsoft.Json;
using System.IO;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbs.Outputs
{
    [ConfigDoc("Writes the configuration used by the scene (FileSuffix, ex. '.used.config'): the result of all its files (the main configuration and the overrides) merged, only the enabled steps and the shared objects they use. To see, or keep, what was used for this scene.",
        Advice = "Keep CanBeUpdated: written again at each run (the configuration may have changed).")]
    public class OutputUsedConfig : SubtitleOutput
    {
        [ConfigDoc("The suffix of the file written, next to the scene (ex. \".used.config\").")]
        [JsonProperty(Order = 5, Required = Required.Always)]
        public string FileSuffix { get; set; } = ".used.config";
        [ConfigDoc("The disabled steps are left out (false: all the steps, also the disabled ones).")]
        [JsonProperty(Order = 6)]
        public bool RemoveUnusedTasks { get; set; } = true;
        [ConfigDoc("The shared objects that no step written uses are left out (ex. the prompts of another workflow).")]
        [JsonProperty(Order = 7)]
        public bool RemoveUnusedSharedObjects { get; set; } = true;

        protected override void DescribeDataFlow(Infra.WorkerDataFlow flow)
        {
            base.DescribeDataFlow(flow);
            // (what it writes: the configuration files of the scene, merged)
            flow.Input(Infra.DataKind.File, "configuration files", "merged");
            flow.Output(Infra.DataKind.File, this.FileSuffix);
        }

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
            File.WriteAllText(targetPath, finalContent, Encoding.UTF8);
        }
    }
}