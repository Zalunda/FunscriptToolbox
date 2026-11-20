using System;

namespace FunscriptToolbox.SubtitlesVerbs.Progress
{
    /// <summary>
    /// The data of the 'Timing' editor of the web UI, for a scene: the subtitles of a review (ex. of '.manual-input.srt', see TranscriberInteractifReview),
    /// the audio (waveform, terrain, sound) and the light videos ('.FAST.mp4') of its parts.
    /// The times are always in the time of the whole scene (all its parts, one after the other).
    /// Called from the threads of the web server (never with the gate of the output held).
    /// An error that the user can understand (ex. "not loaded yet") is a <see cref="TimingEditorException"/>.
    /// </summary>
    public interface ITimingEditor
    {
        /// <summary>What can be edited and played: the reviews, the parts (and their light video), the audios, where the terrain settings come from.</summary>
        object GetInfo();

        /// <summary>The subtitles of a review (null: the review hasn't started yet). The 'stamp' says which version was read.</summary>
        object LoadSubtitles(string reviewId);

        /// <summary>Where the sound is, in an audio (speech, breath, click). Computed once (it can take a moment), then kept.</summary>
        object GetTerrain(string audioId);

        /// <summary>
        /// Saves the subtitles of a review (in the .wipsubtitles). Returns the new stamp.
        /// Refused (<see cref="TimingEditorConflictException"/>) when it was saved from somewhere else since it was read ('stamp'), unless 'force'.
        /// </summary>
        string SaveSubtitles(string reviewId, string stamp, bool force, TimingEditorSubtitle[] subtitles);

        /// <summary>
        /// Saves which subtitles the user validated, known by their timing as saved ("start ms-end ms").
        /// </summary>
        void SaveValidated(string reviewId, string[] keys);

        /// <summary>Time of active work of the user in the editor, to add to the review (see TranscriberInteractifReview.JobState.ActiveTime).</summary>
        void AddActiveTime(string reviewId, long ms);

        /// <summary>The user is done with the review: the worker writes the file at its next run (the scene is rescanned).</summary>
        void MarkReviewDone(string reviewId);

        /// <summary>The waveform of a part of an audio: for each bin, its min and its max (2 signed 16-bit values, little endian).</summary>
        byte[] GetPeaks(string audioId, TimeSpan start, TimeSpan end, int nbBins);

        /// <summary>The whole audio (mono, 16 bits, little endian).</summary>
        (int samplingRate, byte[] pcm) GetAudio(string audioId);

        /// <summary>The light video of a part (its '.FAST.mp4'), null when there is none (then only the audio can be played).</summary>
        string GetVideoPath(int partIndex);
    }

    /// <summary>A subtitle, as sent by the page (times in ms, in the time of the whole scene; the lines as they are in the file).</summary>
    public class TimingEditorSubtitle
    {
        public long S { get; set; }
        public long E { get; set; }
        public string[] Lines { get; set; }
    }

    /// <summary>An error of the timing editor that is shown to the user as is (ex. "The scene is not loaded yet.").</summary>
    public class TimingEditorException : Exception
    {
        public TimingEditorException(string message) : base(message) { }
    }

    /// <summary>The file changed on disk since the page read it (ex. edited in another program).</summary>
    public class TimingEditorConflictException : TimingEditorException
    {
        public TimingEditorConflictException(string message) : base(message) { }
    }
}
