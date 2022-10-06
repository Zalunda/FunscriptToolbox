using AudioSynchronization;
using System;
using System.Collections.Generic;

namespace FunscriptToolbox.AudioSyncVerbs
{
    internal abstract class VerbAudioSync : Verb
    {
        protected const string NotesSynchronizedByFunscriptToolbox = "This is not the original funscript file. It's a synchronized version for a different video, done by " + ApplicationName + ".";

        protected VerbAudioSync(OptionsBase options)
            : base(options)
        { 
        }

        protected FunscriptAction[] TransformsActions(AudioOffsetCollection audioOffsets, FunscriptAction[] originalActions)
        {
            var newActions = new List<FunscriptAction>();
            foreach (var action in originalActions)
            {
                var newAt = audioOffsets.TransformPosition(TimeSpan.FromMilliseconds(action.At));
                if (newAt != null)
                {
                    newActions.Add(new FunscriptAction { At = (int)newAt.Value.TotalMilliseconds, Pos = action.Pos });
                }
            }

            foreach (var item in audioOffsets)
            {
                if (item.Offset == null)
                    WriteInfo($"   From {FormatTimeSpan(item.Start),-12} to {FormatTimeSpan(item.End),-12}, {item.NbTimesUsed,5} actions have been DROPPED");
                else if (item.Offset == TimeSpan.Zero)
                    WriteInfo($"   From {FormatTimeSpan(item.Start),-12} to {FormatTimeSpan(item.End),-12}, {item.NbTimesUsed,5} actions copied as is");
                else
                    WriteInfo($"   From {FormatTimeSpan(item.Start),-12} to {FormatTimeSpan(item.End),-12}, {item.NbTimesUsed,5} actions have been MOVED by {FormatTimeSpan(item.Offset.Value)}");
            }
            WriteInfo();

            return newActions.ToArray();
        }
    }
}
