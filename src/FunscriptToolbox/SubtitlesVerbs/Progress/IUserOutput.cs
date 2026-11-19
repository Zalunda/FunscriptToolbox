using System;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Progress
{
    public class UserPrompt
    {
        public string Title { get; set; }
        public string Message { get; set; }
        /// <summary>When not null, the user can edit this text before answering.</summary>
        public string EditableText { get; set; }
        public string[] Choices { get; set; }
    }

    public class UserAnswer
    {
        public string Choice { get; set; }
        public string Text { get; set; }
    }

    /// <summary>How a first-level node (a video/vseq) is shown in the UI.</summary>
    public class RootNodeInfo
    {
        /// <summary>The short name shown everywhere in the UI (null: the name of the node).</summary>
        public string ShortName { get; set; }

        /// <summary>The metadata extracted from the path (see SubtitleGeneratorGlobalSettings.SceneMetadata), shown in the tooltip.</summary>
        public IReadOnlyDictionary<string, string> Metadata { get; set; }

        /// <summary>A problem with the configuration of the short name / metadata (shown on the node), or null.</summary>
        public string ConfigError { get; set; }
    }

    /// <summary>
    /// Everything the subtitle creation process shows to the user.
    /// Implementations: console (sequential mode) and web page (--ui mode).
    /// The "current task" comes from <see cref="TaskScope.Current"/>.
    /// </summary>
    public interface IUserOutput
    {
        /// <summary>True when the output is the web UI (several sequences are processed in parallel).</summary>
        bool IsWebUi { get; }

        /// <summary>Creates a first-level node (one per video/vseq). Null node in console mode.</summary>
        /// <param name="info">How the node is shown (short name, metadata), or null.</param>
        ITaskNode CreateRootNode(string kind, string name, RootNodeInfo info = null);

        void WriteInfo(string message, ConsoleColor? color, bool isProgress);
        void WriteVerbose(string message, ConsoleColor? color, bool isProgress, bool isVerboseEnabled);
        void WriteError(string message);

        /// <summary>Raw text, without line ending added (ex. tokens of an AI response as they are received).</summary>
        void WriteStream(string text);
        void WriteStreamLine(string text);

        /// <summary>True once each time the user asks to stop what is currently done (Q key, or a button of the UI).</summary>
        bool PollStopRequest();

        /// <summary>Blocks until the user answers. Only supported when <see cref="IsWebUi"/> is true.</summary>
        UserAnswer Ask(UserPrompt prompt);

        /// <summary>
        /// Why the scenes must pause at their next safe point (between two steps: what was received is saved), or null.
        /// Web UI: 'Close' while scenes are running (until it closes, or until the user cancels the close), or 'Pause all' (server).
        /// </summary>
        string PauseAtSafePointReason { get; }

        /// <summary>Counts a scene paused at a safe point while closing (dispose when it continues). Null when not supported.</summary>
        IDisposable EnterPauseForClose();
    }
}
