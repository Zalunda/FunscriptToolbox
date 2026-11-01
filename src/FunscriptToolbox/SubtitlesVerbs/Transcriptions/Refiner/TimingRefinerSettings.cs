using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    /// <summary>
    /// Settings of the refining of the timings (see <see cref="TimingRefiner"/>): the Start and End of each subtitle are judged
    /// by the AI, between the choices that the terrain of the audio offers.
    /// </summary>
    public class TimingRefinerSettings
    {
        /// <summary>false: the timings are kept as they are (nothing else of this class is used).</summary>
        [JsonProperty(Order = 1)]
        public bool Enabled { get; set; } = true;

        /// <summary>The audio extraction used to map the terrain (where the speech is). Required when Enabled.</summary>
        [JsonProperty(Order = 2)]
        public string SourceAudioIdForTerrain { get; set; }

        [JsonProperty(Order = 3)]
        public float ValleyReach { get; set; } = 0.25f;

        /// <summary>START pass only: all clips end this long after the initial End (so the AI hears the whole line).</summary>
        [JsonProperty(Order = 4)]
        public TimeSpan StartPassTailPadding { get; set; } = TimeSpan.FromSeconds(0.8);

        [JsonProperty(Order = 5)]
        public TimeSpan MaxExpansion { get; set; } = TimeSpan.FromSeconds(2.0);

        [JsonProperty(Order = 6)]
        public TimeSpan BleedDetectionMargin { get; set; } = TimeSpan.FromSeconds(3.0);

        /// <summary>After the AI's final Start answer, move the Start forward over pure silence (according to the terrain).</summary>
        [JsonProperty(Order = 7)]
        public bool TrimSilenceOnStart { get; set; } = false;

        /// <summary>After the AI's final End answer, move the End back over pure silence (according to the terrain).</summary>
        [JsonProperty(Order = 8)]
        public bool TrimSilenceOnEnd { get; set; } = true;

        /// <summary>Prompt/batching/extractors of the START pass (must produce BestStart, optionally HeardText). Required when Enabled.</summary>
        [JsonProperty(Order = 20)]
        public AIOptions InnerOptionsStart { get; set; } = new AIOptions();

        /// <summary>Prompt/batching/extractors of the END pass (must produce BestEnd). Required when Enabled.</summary>
        [JsonProperty(Order = 21)]
        public AIOptions InnerOptionsEnd { get; set; } = new AIOptions();

        public AIOptions InnerOptionsFor(BoundarySide side)
            => side == BoundarySide.Start ? this.InnerOptionsStart : this.InnerOptionsEnd;

        /// <summary>A clear error when something needed is not configured (nothing is checked when not Enabled).</summary>
        public void Validate()
        {
            if (!Enabled)
                return;

            if (string.IsNullOrWhiteSpace(SourceAudioIdForTerrain))
                throw new InvalidOperationException("RefineTiming is enabled but 'SourceAudioIdForTerrain' is not set.");
            if (InnerOptionsStart == null || InnerOptionsEnd == null)
                throw new InvalidOperationException("RefineTiming is enabled but 'InnerOptionsStart' or 'InnerOptionsEnd' is not set.");
        }
    }
}
