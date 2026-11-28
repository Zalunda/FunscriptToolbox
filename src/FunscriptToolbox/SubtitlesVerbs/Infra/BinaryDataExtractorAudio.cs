using Newtonsoft.Json;
using System;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    [ConfigDoc("Adds to each subtitle sent to the AI its audio clip, cut from an audio extraction (SourceAudioId), with a margin before / after (ExpandStart / ExpandEnd).")]
    public class BinaryDataExtractorAudio : BinaryDataExtractor
    {
        public override BinaryDataType DataType => BinaryDataType.Audio;

        [ConfigDoc("The id of the audio the clips are cut from (the Id of an audio extraction, usually \"audio\").")]
        [JsonProperty(Order = 10, Required = Required.Always)]
        public string SourceAudioId { get; internal set; }

        [ConfigDoc("Each clip starts this long before the subtitle.")]
        [JsonProperty(Order = 11)]
        public TimeSpan ExpandStart { get; set; } = TimeSpan.Zero;
        [ConfigDoc("Each clip ends this long after the subtitle.")]
        [JsonProperty(Order = 12)]
        public TimeSpan ExpandEnd { get; set; } = TimeSpan.Zero;

        [ConfigDoc("When the gap to the next subtitle is smaller than this, the clip goes up to the next subtitle (the end of a line is not lost when the timing is a bit short).")]
        [JsonProperty(Order = 13)]
        public TimeSpan FillGapSmallerThen { get; set; } = TimeSpan.Zero;
    }
}