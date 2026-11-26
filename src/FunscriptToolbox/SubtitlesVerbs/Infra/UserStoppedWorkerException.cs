using System;
using System.Runtime.Serialization;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    [Serializable]
    internal class UserStoppedWorkerException : Exception
    {
        public UserStoppedWorkerException()
            : base("User stopped process.")
        {
        }

        public UserStoppedWorkerException(string message) : base(message)
        {
        }

        public UserStoppedWorkerException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// The scene is started again ('Redo this step' while it was running or on hold): thrown at its next safe point (see
    /// SubtitleGeneratorContext.PauseIfClosing), it goes up like a stop of the user, up to the end of the thread of the scene.
    /// </summary>
    [Serializable]
    internal class SceneRestartException : UserStoppedWorkerException
    {
        public SceneRestartException()
            : base("The scene is started again.")
        {
        }
    }
}