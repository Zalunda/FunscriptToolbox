namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    /// <summary>Where a candidate comes from.</summary>
    public enum TimingOrigin
    {
        /// <summary>One of the subtitles given to the task.</summary>
        Original,

        /// <summary>A part of a long subtitle that was split (no text of its own: the Start pass gives it one).</summary>
        SplitPart,

        /// <summary>A title card found in the video (no text: its on-screen text is read later, see {GrabOnScreenText}); never refined or checked.</summary>
        TitleCard,
    }
}
