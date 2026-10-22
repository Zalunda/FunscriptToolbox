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
        public TimeSpan ExpandStart { get; set; } = TimeSpan.Zero;
        [JsonProperty(Order = 12)]
        public TimeSpan ExpandEnd { get; set; } = TimeSpan.Zero;

        [JsonProperty(Order = 13)]
        public TimeSpan FillGapSmallerThen { get; set; } = TimeSpan.Zero;
    }
}