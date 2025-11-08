using Newtonsoft.Json;
using System;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class BinaryDataExtractorAudio : BinaryDataExtractor
    {
        public override BinaryDataType DataType => BinaryDataType.Audio;

        [JsonProperty(Order = 10, Required = Required.Always)]
        public string SourceAudioId { get; internal set; }

        [JsonProperty(Order = 11)]
        public TimeSpan FillGapSmallerThen { get; set; } = TimeSpan.Zero;
    }
}