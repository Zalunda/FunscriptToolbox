using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;

namespace FunscriptToolbox.SubtitlesVerbs.Outputs
{
    public abstract class SubtitleOutput : SubtitleWorker
    {
        protected override string GetDefaultProduces() => "Output";
        protected override string GetDefaultVerb() => "Creating";

        protected override void EnsureDataObjectExists(SubtitleGeneratorContext context)
        {
            // Output do not create DataObject in .wipsubtitles file
        }
    }
}