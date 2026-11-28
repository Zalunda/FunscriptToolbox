using System;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    /// <summary>
    /// The step can't go on without something only the user can give: a value of the configuration still to set (ex. a path that is
    /// still "[TOREPLACE-...]", an AI engine still the placeholder), an API key that is not in the .private.config. Not an error: the step
    /// is blocked, and the scene is in 'needs you' in the web UI, with a button for it (see UserAction).
    /// </summary>
    public class NeedsUserException : Exception
    {
        /// <summary>A value of the configuration of the step to set (the path of its properties, ex. "TranscriberTool/ApplicationFullPath").</summary>
        public const string ConfigActionPrefix = "config:";

        /// <summary>A value of the whole configuration of the scene to set, ex. one of another step that has no box in the 'Flow' view (an
        /// alias): the path from its root, ex. "Workers/[12]/SourceId".</summary>
        public const string ConfigAllActionPrefix = "configall:";

        /// <summary>An API key to enter (its name in the .private.config, ex. "APIKeyGemini"; several, separated by ',': at least one).</summary>
        public const string PrivateKeyActionPrefix = "privatekey:";

        /// <summary>What the web UI offers (see ITaskNode.SetUserAction): ConfigActionPrefix / ConfigAllActionPrefix + path, or PrivateKeyActionPrefix + names.</summary>
        public string UserAction { get; }

        public NeedsUserException(string message, string userAction)
            : base(message)
        {
            this.UserAction = userAction;
        }
    }
}
