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
        /// <summary>false: the timings are kept as they are (no terrain, no AI; the validation can't be used).</summary>
        [JsonProperty(Order = 1)]
        public bool Enabled { get; set; } = true;

        /// <summary>The audio extraction used to map the terrain (where the speech is). Required when Enabled.</summary>
        [JsonProperty(Order = 2)]
        public string SourceAudioIdForTerrain { get; set; }

        [JsonProperty(Order = 3)]
        public float ValleyReach { get; set; } = 0.25f;

        /// <summary>After the final Start answer, move the Start forward over pure silence (according to the terrain).</summary>
        [JsonProperty(Order = 4)]
        public bool TrimSilenceOnStart { get; set; } = false;

        /// <summary>After the final End answer, move the End back over pure silence (according to the terrain).</summary>
        [JsonProperty(Order = 5)]
        public bool TrimSilenceOnEnd { get; set; } = true;

        /// <summary>The text of a neighbour closer than this is sent with a request, as a possible bleed of its voice in the clip.</summary>
        [JsonProperty(Order = 6)]
        public TimeSpan BleedDetectionMargin { get; set; } = TimeSpan.FromSeconds(3.0);

        /// <summary>
        /// How the timings snapped on the terrain are refined (the cost in AI tokens): TimingRefineEngineNone (nothing more, no AI),
        /// TimingRefineEngineVerify (one request per subtitle: does the clip contain the text?), TimingRefineEngineStartEndJudge
        /// (the AI picks the best Start, then the best End, among the options of the terrain), TimingRefineEngineSpokenMarkers
        /// (markers spoken in the audio at the options of the terrain, the AI transcribes them around the line). Required when Enabled.
        /// </summary>
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
