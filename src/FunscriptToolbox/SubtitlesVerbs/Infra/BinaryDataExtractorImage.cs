using Newtonsoft.Json;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class BinaryDataExtractorImage : BinaryDataExtractor
    {
        public override BinaryDataType DataType => BinaryDataType.Image;

        [ConfigDoc("The ffmpeg filter applied to each screenshot (taken in the middle of the subtitle): ex. a crop of the left eye of a VR video, a resize, the time written on the image " +
            "(drawtext with '[STARTTIME]', so that the AI can anchor what it sees to the right node).")]
        [JsonProperty(Order = 10)]
        internal string FfmpegFilter { get; set; }
    }
}