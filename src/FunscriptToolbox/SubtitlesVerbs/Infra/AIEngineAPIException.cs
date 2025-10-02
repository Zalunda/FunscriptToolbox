using System;
using System.Runtime.Serialization;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    [Serializable]
    internal class AIEngineAPIException : Exception
    {
        private AggregateException ex;
        private object value;
        private string v;

        public AIEngineAPIException()
        {
        }

        public AIEngineAPIException(string message) : base(message)
        {
        }

        public AIEngineAPIException(string message, Exception innerException) : base(message, innerException)
        {
        }

        protected AIEngineAPIException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}