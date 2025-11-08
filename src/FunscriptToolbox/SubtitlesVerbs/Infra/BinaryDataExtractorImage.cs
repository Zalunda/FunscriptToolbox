using Newtonsoft.Json;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class BinaryDataExtractorImage : BinaryDataExtractor
    {
        public override BinaryDataType DataType => BinaryDataType.Image;

        [JsonProperty(Order = 10)]
        internal string FfmpegFilter { get; set; }
    }
}