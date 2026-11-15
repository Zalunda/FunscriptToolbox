using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    /// <summary>
    /// Settings of the refining of the timings (see <see cref="TimingRefiner"/>): the Start and End of each subtitle are judged
    /// by the AI, between the choices that the terrain of the audio offers.
    /// </summary>
    public enum RefineMode
    {
        /// <summary>The timings are kept as they are: no terrain, no AI.</summary>
        Off,

        /// <summary>The initial snap on the terrain of the audio only: no AI request at all (no tokens).</summary>
        SnapOnly,

        /// <summary>The initial snap, then ONE request per subtitle with ONE clip (exactly the snapped start to the snapped end, no padding) and its text: does the clip contain the text? The timings are kept either way.</summary>
        SnapAndVerify,

        /// <summary>The initial snap, then the AI picks the best Start and the best End among the options of the terrain (several requests per subtitle).</summary>
        Full,
    }

    public class TimingRefinerSettings
    {
        /// <summary>
        /// How much is done to the timings (from the cheapest to the most expensive in AI tokens):
        /// Off, SnapOnly, SnapAndVerify or Full. See <see cref="RefineMode"/>.
        /// </summary>
        [JsonProperty(Order = 1)]
        [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
        public RefineMode Mode { get; set; } = RefineMode.Full;

        /// <summary>The terrain of the audio is used (every mode except Off).</summary>
        [JsonIgnore]
        public bool Enabled => Mode != RefineMode.Off;

        /// <summary>The AI is asked something (SnapAndVerify and Full).</summary>
        [JsonIgnore]
        public bool UsesAI => Mode == RefineMode.Full || Mode == RefineMode.SnapAndVerify;

        /// <summary>The audio extraction used to map the terrain (where the speech is). Required when Enabled.</summary>
        [JsonProperty(Order = 2)]
        public string SourceAudioIdForTerrain { get; set; }

        [JsonProperty(Order = 3)]
        public float ValleyReach { get; set; } = 0.25f;

        /// <summary>START pass only: all clips end this long after the initial End (so the AI hears the whole line).</summary>
        [JsonProperty(Order = 4)]
        public TimeSpan StartPassTailPadding { get; set; } = TimeSpan.FromSeconds(0.8);

        /// <summary>
        /// START pass only: every clip starts with at least this much silence (the AI needs a moment to settle in).
        /// The silence that really precedes the clip is used first; when it is shorter, the rest is stolen from the nearest
        /// other silence of the audio and spliced in front (never digital silence, the AI could react differently to it).
        /// Zero = no padding.
        /// </summary>
        [JsonProperty(Order = 13)]
        public TimeSpan StartClipPadding { get; set; } = TimeSpan.Zero;

        /// <summary>
        /// END pass only: every clip ends with at least this much silence (the AI understands a sound a moment after hearing it,
        /// so it needs to hear what follows the end to recognise the last syllable). Same rules as StartClipPadding.
        /// Zero = no padding.
        /// </summary>
        [JsonProperty(Order = 14)]
        public TimeSpan EndClipPadding { get; set; } = TimeSpan.Zero;

        [JsonProperty(Order = 5)]
        public TimeSpan MaxExpansion { get; set; } = TimeSpan.FromSeconds(2.0);

        [JsonProperty(Order = 9)]
        public int MaxDipsInward { get; set; } = 100;

        [JsonProperty(Order = 10)]
        public int MaxDipsOutward { get; set; } = 100;

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
        public int MaxTestsInRequest { get; set; } = 4;

        /// <summary>Prompt/batching/extractors of the single verification request (SnapAndVerify): one clip, the snapped Start to the snapped End, and the text. Required in that mode.</summary>
        [JsonProperty(Order = 22)]
        public AIOptions InnerOptionsVerify { get; set; } = new AIOptions();

        /// <summary>
        /// Optional (null = not done, Full mode only). After the End pass, when the End chosen by the AI is later than the closest
        /// option before it by at least EndTailMinExtra, ONE more request asks what that extra audio is (the text is not sent):
        /// Speech, Silence, Breathing, Laughter, Moaning or Noise. Anything but Speech moves the End back to that option
        /// (then the new End is checked the same way). Must produce "TailKind".
        /// </summary>
        [JsonProperty(Order = 23)]
        public AIOptions InnerOptionsEndTail { get; set; }

        /// <summary>The extra audio appended by the chosen End must be at least this long to be checked (a shorter one is not worth a request).</summary>
        [JsonProperty(Order = 15)]
        public TimeSpan EndTailMinExtra { get; set; } = TimeSpan.FromMilliseconds(400);

        /// <summary>How many times in a row the End of a subtitle can be moved back by the tail check.</summary>
        [JsonProperty(Order = 16)]
        public int EndTailMaxChecks { get; set; } = 1;

        /// <summary>
        /// By answer of the AI (TailKind): when the terrain finds Speech over at least this fraction of the extra audio (0 to 1),
        /// the answer is not trusted and the End is kept. Ex: "Silence" over loud audio is wrong; "Moaning" over continuous speech is
        /// often the last word(s) misidentified. A kind that is not listed always moves the End back.
        /// </summary>
        [JsonProperty(Order = 17, ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public Dictionary<string, float> EndTailKeepIfSpeechRatio { get; set; } = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
        {
            { "Silence", 0.3f },
            { "Moaning", 0.5f },
        };

        /// <summary>The tail check is done.</summary>
        [JsonIgnore]
        public bool TailCheckEnabled => Mode == RefineMode.Full && InnerOptionsEndTail != null;

        /// <summary>The options of the requests of a side (in SnapAndVerify, the single verification request is a Start request).</summary>
        public AIOptions InnerOptionsFor(BoundarySide side)
            => Mode == RefineMode.SnapAndVerify ? this.InnerOptionsVerify
                : side == BoundarySide.Start ? this.InnerOptionsStart : this.InnerOptionsEnd;

        /// <summary>The options that the mode uses (the others are not needed).</summary>
        [JsonIgnore]
        public AIOptions[] InnerOptionsUsed => Mode == RefineMode.Full ? new[] { InnerOptionsStart, InnerOptionsEnd }.Concat(InnerOptionsEndTail != null ? new[] { InnerOptionsEndTail } : new AIOptions[0]).ToArray()
            : Mode == RefineMode.SnapAndVerify ? new[] { InnerOptionsVerify }
            : new AIOptions[0];

        /// <summary>A clear error when something needed is not configured (nothing is checked when not Enabled).</summary>
        public void Validate()
        {
            if (!Enabled)
                return;

            if (string.IsNullOrWhiteSpace(SourceAudioIdForTerrain))
                throw new InvalidOperationException($"RefineTiming Mode is '{Mode}' but 'SourceAudioIdForTerrain' is not set.");
            if (Mode == RefineMode.Full && (InnerOptionsStart == null || InnerOptionsEnd == null))
                throw new InvalidOperationException("RefineTiming Mode is 'Full' but 'InnerOptionsStart' or 'InnerOptionsEnd' is not set.");
            if (Mode == RefineMode.SnapAndVerify && InnerOptionsVerify == null)
                throw new InvalidOperationException("RefineTiming Mode is 'SnapAndVerify' but 'InnerOptionsVerify' is not set.");

            if (EndTailKeepIfSpeechRatio != null && EndTailKeepIfSpeechRatio.Values.Any(v => v < 0f || v > 1f))
                throw new InvalidOperationException("RefineTiming 'EndTailKeepIfSpeechRatio' values must be between 0 and 1.");
            if (EndTailMinExtra < TimeSpan.Zero || EndTailMaxChecks < 1)
                throw new InvalidOperationException("RefineTiming 'EndTailMinExtra' can't be negative and 'EndTailMaxChecks' must be at least 1.");

            if (StartClipPadding < TimeSpan.Zero || EndClipPadding < TimeSpan.Zero)
                throw new InvalidOperationException("RefineTiming 'StartClipPadding' and 'EndClipPadding' can't be negative.");

            // The padded clips are built by the refiner itself: the expansion of the extractor would be ignored
            foreach (var (side, padding) in new[] { (BoundarySide.Start, StartClipPadding), (BoundarySide.End, EndClipPadding) })
            {
                if (padding <= TimeSpan.Zero || Mode != RefineMode.Full)
                    continue;

                foreach (var extractor in InnerOptionsFor(side).BinaryDataExtractors?.OfType<BinaryDataExtractorAudio>() ?? Enumerable.Empty<BinaryDataExtractorAudio>())
                {
                    if (extractor.ExpandStart != TimeSpan.Zero || extractor.ExpandEnd != TimeSpan.Zero || extractor.FillGapSmallerThen != TimeSpan.Zero)
                        throw new InvalidOperationException(
                            $"RefineTiming has a clip padding for the {side} pass, but its audio extractor '{extractor.OutputFieldName}' has ExpandStart, ExpandEnd or FillGapSmallerThen set: " +
                            "they can't be combined (set them to 0 and use StartClipPadding / EndClipPadding).");
                }
            }
        }
    }
}
