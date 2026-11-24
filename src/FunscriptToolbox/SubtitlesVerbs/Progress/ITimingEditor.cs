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
        /// Saves the subtitles of a review, and the list of its speakers (in the .wipsubtitles). Returns the new stamp.
        /// Refused (<see cref="TimingEditorConflictException"/>) when it was saved from somewhere else since it was read ('stamp'), unless 'force'.
        /// </summary>
        string SaveSubtitles(string reviewId, string stamp, bool force, TimingEditorSubtitle[] subtitles, TimingEditorSpeaker[] speakers);

        /// <summary>
        /// Saves which subtitles have their timing finalized by the user, the subtitles known by their timing as saved ("start ms-end ms").
        /// </summary>
        void SaveFinalized(string reviewId, TimingEditorFinalized[] items);

        /// <summary>
        /// The parts of the scene listened to by the user to validate the gaps (see TranscriberInteractifReview.JobState.ValidatedGaps):
        /// they replace the ones saved before.
        /// </summary>
        void SaveValidatedGaps(string reviewId, TimingEditorRange[] ranges);

        /// <summary>Time of active work of the user in the editor, to add to the review (see TranscriberInteractifReview.JobState.ActiveTime).</summary>
        void AddActiveTime(string reviewId, long ms);

        /// <summary>The user is done with the review: the worker writes the file at its next run (the scene is rescanned).</summary>
        void MarkReviewDone(string reviewId);

        /// <summary>The waveform of a part of an audio: for each bin, its min and its max (2 signed 16-bit values, little endian).</summary>
        byte[] GetPeaks(string audioId, TimeSpan start, TimeSpan end, int nbBins);

        /// <summary>The whole audio (mono, 16 bits, little endian).</summary>
        (int samplingRate, byte[] pcm) GetAudio(string audioId);

        /// <summary>
        /// The video of a part: its light video ('.FAST.mp4'), otherwise the video itself (original: the video itself, otherwise its light video);
        /// null when there is none (then only the audio can be played).
        /// </summary>
        string GetVideoPath(int partIndex, bool original);
    }

    /// <summary>A subtitle, as sent by the page (times in ms, in the time of the whole scene; the lines as they are in the file).</summary>
    public class TimingEditorSubtitle
    {
        public long S { get; set; }
        public long E { get; set; }
        public string[] Lines { get; set; }
        /// <summary>Its timing is finalized by the user (null: not sent, ex. an older page: what was saved for this timing is kept).</summary>
        public bool? F { get; set; }
        /// <summary>Its speaker is chosen / confirmed by the user (null: not sent: what was saved for this timing is kept).</summary>
        public bool? Sc { get; set; }
    }

    /// <summary>Whether the timing of a subtitle is finalized, the subtitle known by its timing as saved (K = "start ms-end ms").</summary>
    public class TimingEditorFinalized
    {
        public string K { get; set; }
        public bool F { get; set; }
    }

    /// <summary>A part of the scene, as sent by the page (times in ms, in the time of the whole scene).</summary>
    public class TimingEditorRange
    {
        public long S { get; set; }
        public long E { get; set; }
    }

    /// <summary>A speaker of the list of the editor (its order is the order of the list: keys 1-9).</summary>
    public class TimingEditorSpeaker
    {
        /// <summary>The name written in the metadata (ex. {Speaker:Nao Jinguuji}).</summary>
        public string Name { get; set; }
        /// <summary>A shorter name, only shown in the editor (ex. "Nao"), or null.</summary>
        public string Alias { get; set; }
        /// <summary>An image of the speaker (a part of a frame of the video), only shown in the editor: a data URL (JPEG / PNG), or null.</summary>
        public string Image { get; set; }
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
