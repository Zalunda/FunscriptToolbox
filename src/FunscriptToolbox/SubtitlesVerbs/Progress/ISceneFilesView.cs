namespace FunscriptToolbox.SubtitlesVerbs.Progress
{
    /// <summary>
    /// The 'Files' tab of a scene in the web UI: the files of the scene next to its video(s) and the ones of its backup folder
    /// (with --verbose: the AI requests / responses, the audio clips, the screenshots; always: the old versions of the files).
    /// Called from the threads of the web server.
    /// </summary>
    public interface ISceneFilesView
    {
        /// <summary>{ folders: [{ kind, path, exists }], files: [{ folder, name, type, group, date, size, viewer }], verbose }</summary>
        object List();

        /// <summary>
        /// A file of the list, by the kind of its folder ("scene" / "backup") and its name, exactly as listed (null: not in the list).
        /// A text is sent as text (the long base64 data replaced by a note, the end cut when too long), an image / a sound as it is.
        /// </summary>
        SceneFileContent Read(string folder, string name);
    }

    public class SceneFileContent
    {
        /// <summary>ex. "text/plain; charset=utf-8", "image/jpeg", "audio/wav".</summary>
        public string ContentType { get; set; }

        public byte[] Bytes { get; set; }
    }
}
