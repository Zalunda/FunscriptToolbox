namespace FunscriptToolbox.SubtitlesVerbs.Processors.Refiner
{
    /// <summary>Where a candidate comes from.</summary>
    public enum TimingOrigin
    {
        /// <summary>One of the subtitles given to the task.</summary>
        Original,

        /// <summary>A title card found in the video (no text: its on-screen text is read later, see {GrabOnScreenText}); never snapped or checked.</summary>
        TitleCard,
    }
}
