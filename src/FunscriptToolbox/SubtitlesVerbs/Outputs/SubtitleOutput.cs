using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;

namespace FunscriptToolbox.SubtitlesVerbs.Outputs
{
    public abstract class SubtitleOutput : SubtitleWorker
    {
        [JsonProperty(Order = 2, Required = Required.Always)]
        public string OutputId { get; set; }

        protected override string GetId() => this.OutputId;
        protected override string GetWorkerTypeName() => "Output";
        protected override string GetExecutionVerb() => "Creating";

        protected override void EnsureDataObjectExists(SubtitleGeneratorContext context)
        {
            // Output do not create DataObject in .wipsubtitles file
        }
    }
}