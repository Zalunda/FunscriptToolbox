using Newtonsoft.Json;
using System;

namespace FunscriptToolbox.SubtitlesVerbs.Processors.Refiner
{
    /// <summary>
    /// Settings for snapping subtitle boundaries on the terrain of the audio (see <see cref="TimingRefiner"/>).
    /// </summary>
    public class TimingRefinerSettings
    {
        [ConfigDoc("Snaps the Start and End of each subtitle on the terrain of the audio (where the speech is).\n" +
            "false: the timings are kept as they are (no terrain; Validation can't be used).")]
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

        /// <summary>A clear error when something needed is not configured (nothing is checked when not Enabled).</summary>
        public void Validate()
        {
            if (!this.Enabled)
                return;

            if (string.IsNullOrWhiteSpace(this.SourceAudioIdForTerrain))
                throw new InvalidOperationException("RefineTiming is enabled but 'SourceAudioIdForTerrain' is not set.");
        }
    }
}
