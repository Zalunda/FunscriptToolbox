using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscriberNotReadyException : Exception
    {
        public string Reason { get; }
        public string[] UserTodos { get; internal set; }

        public TranscriberNotReadyException(string reason, IEnumerable<string> userTodos = null)
        {
            this.Reason = reason;
            this.UserTodos = userTodos?.ToArray() ?? Array.Empty<string>();
        }
    }
}