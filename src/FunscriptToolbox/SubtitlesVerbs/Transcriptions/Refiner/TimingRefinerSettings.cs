using Newtonsoft.Json;
using System;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    /// <summary>
    /// Settings of the refining of the timings (see <see cref="TimingRefiner"/>): the boundaries of each subtitle are snapped on the
    /// terrain of the audio (where the speech is), then refined by the engine (<see cref="Engine"/>: which questions are asked to the AI).
    /// </summary>
    public class TimingRefinerSettings
    {
        [ConfigDoc("Refines the Start and End of each subtitle: first snapped on the terrain of the audio (where the speech is), then refined by the Engine.\n" +
            "false: the timings are kept as they are (no terrain, no AI; Validation can't be used).")]
        [JsonProperty(Order = 1)]
        public bool Enabled { get; set; } = true;

        [ConfigDoc("The audio used to map the terrain (where the speech is): the Id of an audio extraction (ex. \"audio\"). Required when Enabled.")]
        [JsonProperty(Order = 2)]
        public string SourceAudioIdForTerrain { get; set; }

        [ConfigDoc("The terrain: inside a stretch of sound, a dip of the volume is a cut (a possible boundary) when it goes down at least to this fraction of the way " +
            "from the noise floor to the lower of the two peaks around it (0 to 1). Lower = fewer cuts.",
            Advice = "Leave it as it is, unless the boundaries offered on the terrain (Review editor) are clearly too many / too few.")]
        [JsonProperty(Order = 3)]
        public float ValleyReach { get; set; } = 0.25f;

        [ConfigDoc("After the final Start is chosen, it's moved forward over the pure silence that follows it (according to the terrain).")]
        [JsonProperty(Order = 4)]
        public bool TrimSilenceOnStart { get; set; } = false;

        [ConfigDoc("After the final End is chosen, it's moved back over the pure silence that precedes it (according to the terrain).")]
        [JsonProperty(Order = 5)]
        public bool TrimSilenceOnEnd { get; set; } = true;

        [ConfigDoc("The text of a neighbour subtitle closer than this is sent with the request (its voice may be heard in the clip): the AI knows that those words are not part of the line.")]
        [JsonProperty(Order = 6)]
        public TimeSpan BleedDetectionMargin { get; set; } = TimeSpan.FromSeconds(3.0);

        [ConfigDoc("How the timings snapped on the terrain are refined: the main lever between the quality of the timings and the cost in AI tokens.\n" +
            "  TimingRefineEngineNone: the snap, nothing else (no AI, free).\n" +
            "  TimingRefineEngineVerify: one request per subtitle (does the clip contain the text?): only flags, the timings are kept.\n" +
            "  TimingRefineEngineStartEndJudge: the AI picks the best Start, then the best End, among the options of the terrain (several requests per subtitle).\n" +
            "  TimingRefineEngineSpokenMarkers: markers spoken in the audio at the options of the terrain, the AI transcribes them around the line (a confidence per subtitle).\n" +
            "Required when Enabled.",
            Advice = "TimingRefineEngineNone (shared object \"TimingRefineEngineNone\"): for now, none of the engines that send AI requests is worth its cost " +
                "(a lot of tokens, and the timings still need to be checked in the Review editor). With None, ignore in Validation the codes that fire on almost every subtitle (the text is never checked).")]
        [JsonProperty(Order = 10)]
        public TimingRefineEngine Engine { get; set; }

        /// <summary>The AI is asked something.</summary>
        [JsonIgnore]
        public bool UsesAI => this.Enabled && this.Engine?.UsesAI == true;

        /// <summary>A clear error when something needed is not configured (nothing is checked when not Enabled).</summary>
        public void Validate()
        {
            if (!this.Enabled)
                return;

            if (string.IsNullOrWhiteSpace(this.SourceAudioIdForTerrain))
                throw new InvalidOperationException("RefineTiming is enabled but 'SourceAudioIdForTerrain' is not set.");
            if (this.Engine == null)
                throw new InvalidOperationException(
                    "RefineTiming is enabled but 'Engine' is not set (ex. { \"$ref\": \"TimingRefineEngineNone\" }, TimingRefineEngineVerify, TimingRefineEngineStartEndJudge or TimingRefineEngineSpokenMarkers).");
            this.Engine.Validate();
        }
    }
}
