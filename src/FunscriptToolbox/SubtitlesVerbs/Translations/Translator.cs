using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Translations
{
    /// <summary>
    /// A step that takes tracks (their timings and metadata, see Metadatas) and produces a new track (ex. an analysis, a translation,
    /// subtitles shaped for reading). See TrackProducer: everything else is common.
    /// </summary>
    public abstract class Transform : TrackProducer
    {
        [ConfigDoc("The language of the text produced (ex. \"en\", \"fr\" for a translation): also what [TranslationLanguage] becomes in the prompts. null: the language of the video.")]
        [JsonProperty(Order = 9)]
        public Language Language { get; set; }

        protected override Language GetTrackLanguage(SubtitleGeneratorContext context) => this.Language ?? context.Config.SourceLanguage;
        protected override string GetDefaultProduces() => "Track";
        protected override string GetDefaultVerb() => "Processing";
    }
}
