using AudioSynchronization;
using FunscriptToolbox.Core;
using log4net;
using System;
using System.Collections.Generic;

namespace FunscriptToolbox.AudioSyncVerbs
{
    internal abstract class VerbAudioSync : Verb
    {
        protected const string NotesSynchronizedByFunscriptToolbox = "This is not the original funscript file. It's a synchronized version for a different video, done by " + ApplicationName + ".";

        protected VerbAudioSync(ILog log, OptionsBase options)
            : base(log, options)
        {
        }

        protected FunscriptAction[] TransformsActions(AudioOffsetCollection audioOffsets, IEnumerable<FunscriptAction> originalActions)
        {
            audioOffsets.ResetUsages();

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
                    WriteInfo($"   From {FormatTimeSpan(item.StartTime),-12} to {FormatTimeSpan(item.EndTime),-12}, {item.NbTimesUsed,5} actions have been DROPPED");
                else if (item.Offset == TimeSpan.Zero)
                    WriteInfo($"   From {FormatTimeSpan(item.StartTime),-12} to {FormatTimeSpan(item.EndTime),-12}, {item.NbTimesUsed,5} actions copied as is");
                else
                    WriteInfo($"   From {FormatTimeSpan(item.StartTime),-12} to {FormatTimeSpan(item.EndTime),-12}, {item.NbTimesUsed,5} actions have been MOVED by {FormatTimeSpan(item.Offset.Value)}");
            }

            return newActions.ToArray();
        }

        protected Subtitle[] TransformsSubtitles(AudioOffsetCollection audioOffsets, IEnumerable<Subtitle> originalSubtitles)
        {
            audioOffsets.ResetUsages();

            var newSubtitles = new List<Subtitle>();
            foreach (var subtitle in originalSubtitles)
            {
                var newStart = audioOffsets.TransformPosition(subtitle.StartTime);
                if (newStart != null)
                {
                    newSubtitles.Add(new Subtitle(newStart.Value, newStart.Value + subtitle.Duration, subtitle.Lines));
                }
            }

            foreach (var item in audioOffsets)
            {
                if (item.Offset == null)
                    WriteInfo($"   From {FormatTimeSpan(item.StartTime),-12} to {FormatTimeSpan(item.EndTime),-12}, {item.NbTimesUsed,5} subtitles have been DROPPED");
                else if (item.Offset == TimeSpan.Zero)
                    WriteInfo($"   From {FormatTimeSpan(item.StartTime),-12} to {FormatTimeSpan(item.EndTime),-12}, {item.NbTimesUsed,5} subtitles copied as is");
                else
                    WriteInfo($"   From {FormatTimeSpan(item.StartTime),-12} to {FormatTimeSpan(item.EndTime),-12}, {item.NbTimesUsed,5} subtitles have been MOVED by {FormatTimeSpan(item.Offset.Value)}");
            }

            return newSubtitles.ToArray();
        }
    }
}
