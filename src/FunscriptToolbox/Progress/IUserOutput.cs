using System;

namespace FunscriptToolbox.Progress
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
        ITaskNode CreateRootNode(string kind, string name);

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
    }
}
