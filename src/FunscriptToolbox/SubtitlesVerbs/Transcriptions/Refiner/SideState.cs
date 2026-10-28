namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    /// <summary>What the heard text says about one side (start or end) of a clip.</summary>
    public enum SideState
    {
        /// <summary>The first/last syllable of the original text was heard.</summary>
        Ok,
        /// <summary>Syllables of the original text are missing on that side (clip probably cuts speech).</summary>
        Missing,
        /// <summary>More was heard than the original text on that side (clip probably includes neighbouring speech).</summary>
        Extra,
        /// <summary>A few syllables were heard in place of the expected ones (speech is there, transcribed differently).</summary>
        Substituted
    }
}
