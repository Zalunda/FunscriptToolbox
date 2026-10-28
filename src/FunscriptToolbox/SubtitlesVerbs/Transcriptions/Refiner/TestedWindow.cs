namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    /// <summary>A window that was sent to the AI, with the answer.</summary>
    public class TestedWindow : TimeWindow
    {
        public int Round { get; set; }
        public string HeardText { get; set; }
        public RomajiSyllableMatchAnalysis Match { get; set; }
    }
}
