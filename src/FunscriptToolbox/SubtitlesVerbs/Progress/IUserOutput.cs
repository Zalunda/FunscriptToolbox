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

        /// <summary>The priority of the scene on the shared resources (0 = the highest, see SubtitleGeneratorGlobalSharedSettings.ScenePriorities).</summary>
        public int PriorityLevel { get; set; }

        /// <summary>Why the scene has this priority (ex. "1st rule: Type ~ 'ManualHQWorkflow-G4'"), or null when there is no rule.</summary>
        public string PriorityText { get; set; }
    }

    /// <summary>
    /// Everything the subtitle creation process shows to the user.
    /// Implementation: the web page (<see cref="WebUiOutput"/>).
    /// The "current task" comes from <see cref="TaskScope.Current"/>.
    /// </summary>
    public interface IUserOutput
    {
        /// <summary>Creates a first-level node (one per video/vseq).</summary>
        /// <param name="info">How the node is shown (short name, metadata), or null.</param>
        ITaskNode CreateRootNode(string kind, string name, RootNodeInfo info = null);

        void WriteInfo(string message, ConsoleColor? color, bool isProgress);
        void WriteVerbose(string message, ConsoleColor? color, bool isProgress, bool isVerboseEnabled);
        void WriteError(string message);

        /// <summary>Raw text, without line ending added (ex. tokens of an AI response as they are received).</summary>
        void WriteStream(string text);
        void WriteStreamLine(string text);

        /// <summary>True once each time the user asks to stop what is currently done (the Stop button of the task in the UI).</summary>
        bool PollStopRequest();

        /// <summary>Blocks until the user answers (a prompt in the web page).</summary>
        UserAnswer Ask(UserPrompt prompt);

        /// <summary>
        /// Why the scenes must pause at their next safe point (between two steps: what was received is saved), or null.
        /// Web UI: 'Close' while scenes are running (until it closes, or until the user cancels the close), or 'Pause all' (server).
        /// </summary>
        string PauseAtSafePointReason { get; }

        /// <summary>The same, for a scene (sceneNodeId: the node of the scene): also when this scene is paused by the user ('Pause scene').</summary>
        string GetPauseAtSafePointReason(int? sceneNodeId);

        /// <summary>Counts a scene paused at a safe point while closing (dispose when it continues). Null when not supported.</summary>
        IDisposable EnterPauseForClose();
    }
}
