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

        protected UserStoppedWorkerException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}