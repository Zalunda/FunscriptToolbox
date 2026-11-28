using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Processors
{
    /// <summary>A step that takes the audio and produces a track in the language of the video (see TrackProducer: everything else is common).</summary>
    public abstract class Transcriber : TrackProducer
    {
        protected override Language GetTrackLanguage(SubtitleGeneratorContext context) => context.Config.SourceLanguage;
        protected override string GetDefaultVerb() => "Transcribing";
    }
}
