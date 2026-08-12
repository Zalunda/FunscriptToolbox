using log4net;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xabe.FFmpeg;
using FunscriptToolbox.Core;
using AudioSynchronization;

namespace FunscriptToolbox.RetimerVerbs
{
    internal abstract class VerbRetimerBase : Verb
    {
        protected VerbRetimerBase(ILog log, OptionsBase options) : base(log, options)
        {
        }

        protected IMediaInfo GetMediaInfo(string videoPath)
        {
            if (!File.Exists(videoPath))
                throw new ArgumentException($"Video file '{videoPath}' does not exist.");

            return FFmpeg.GetMediaInfo(videoPath).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Synchronizes sidecar files (.srt and .funscript) using the generated offsets map.
        /// Can be called standalone or immediately after rendering the video.
        /// </summary>
        protected void SyncSidecarAssets(string originalVideoPath, string retimedVideoPath, ICollection<RetimerSyncOffset> offsets)
        {
            string originalBasePath = Path.Combine(Path.GetDirectoryName(originalVideoPath) ?? "", Path.GetFileNameWithoutExtension(originalVideoPath));
            string retimedBasePath = Path.Combine(Path.GetDirectoryName(retimedVideoPath) ?? "", Path.GetFileNameWithoutExtension(retimedVideoPath));

            string originalSrt = originalBasePath + SubtitleFile.SrtExtension;
            string originalFunscript = originalBasePath + Funscript.FunscriptExtension;

            // Sync Subtitles
            if (File.Exists(originalSrt))
            {
                WriteInfo($"Synchronizing Subtitles: {originalSrt}");
                var originalSubs = SubtitleFile.FromSrtFile(originalSrt);
                var newSubs = new List<Subtitle>();
                int currentNumber = 1;

                foreach (var sub in originalSubs.Subtitles)
                {
                    var newStart = MapTime(sub.StartTime, offsets);
                    var newEnd = MapTime(sub.EndTime, offsets);

                    // Only keep subtitle if BOTH start and end map successfully to the new timeline
                    if (newStart.HasValue && newEnd.HasValue)
                    {
                        newSubs.Add(new Subtitle(newStart.Value, newEnd.Value, sub.Lines, currentNumber++));
                    }
                }

                string newSrtPath = retimedBasePath + SubtitleFile.SrtExtension;
                var newSubtitleFile = new SubtitleFile(newSrtPath, newSubs);
                newSubtitleFile.SaveSrt(newSrtPath);
                WriteInfo($"Saved synced subtitles ({newSubs.Count} blocks) to: {newSrtPath}");
            }
            else
            {
                WriteInfo("No original .srt found. Skipping subtitle sync.");
            }

            // Sync Funscript
            if (File.Exists(originalFunscript))
            {
                WriteInfo($"Synchronizing Funscript: {originalFunscript}");
                var funscript = Funscript.FromFile(originalFunscript);
                var newActions = new List<FunscriptAction>();

                foreach (var action in funscript.Actions)
                {
                    var newTime = MapTime(action.AtAsTimeSpan, offsets);
                    if (newTime.HasValue)
                    {
                        newActions.Add(new FunscriptAction((int)Math.Round(newTime.Value.TotalMilliseconds), action.Pos));
                    }
                }

                funscript.Actions = newActions.ToArray();

                // Update internal duration metadata to match the new timeline length
                funscript.Duration = (int)Math.Round(offsets.Last().RetimerEndTime.TotalMilliseconds);

                // Update audio signature to match the new audio
                funscript.AudioSignature = Convert(AudioTracksAnalyzer.ExtractSignature(retimedVideoPath));

                // Attempt to map chapters if they exist (fallback to original if unmappable)
                funscript.TransformChaptersTime(t => MapTime(t, offsets) ?? t);
                funscript.AddNotes($"Retimed from original using Retimer Pipeline. Map points: {offsets.Count}");

                string newFunscriptPath = retimedBasePath + Funscript.FunscriptExtension;
                funscript.Save(newFunscriptPath);
                WriteInfo($"Saved synced funscript ({newActions.Count} actions) to: {newFunscriptPath}");
            }
            else
            {
                WriteInfo("No original .funscript found. Skipping funscript sync.");
            }
        }

        /// <summary>
        /// Projects an original timestamp onto the retimed timeline using linear interpolation.
        /// Returns null if the timestamp falls outside the mapped segments.
        /// </summary>
        private TimeSpan? MapTime(TimeSpan originalTime, ICollection<RetimerSyncOffset> offsets)
        {
            // Find the segment this timestamp belongs to.
            // Using < OriginalEndTime because the next segment handles exact boundaries.
            var segment = offsets.FirstOrDefault(o => originalTime >= o.OriginalStartTime && originalTime < o.OriginalEndTime);

            // Edge Case: If exactly at the end of the video/final segment
            if (segment == null)
            {
                var last = offsets.LastOrDefault();
                if (last != null && originalTime == last.OriginalEndTime)
                    return last.RetimerEndTime;
            }

            if (segment == null)
            {
                // Action dropped completely (falls in an unmapped discarded segment)
                return null;
            }

            double originalDurationMs = (segment.OriginalEndTime - segment.OriginalStartTime).TotalMilliseconds;
            if (originalDurationMs <= 0) return segment.RetimerStartTime; // Safety against divide-by-zero

            // Calculate how far into the segment the original time is (0.0 to 1.0)
            double progress = (originalTime - segment.OriginalStartTime).TotalMilliseconds / originalDurationMs;

            // Map that same percentage onto the new retimed segment
            double retimedDurationMs = (segment.RetimerEndTime - segment.RetimerStartTime).TotalMilliseconds;
            double newTimeMs = segment.RetimerStartTime.TotalMilliseconds + (progress * retimedDurationMs);

            return TimeSpan.FromMilliseconds(newTimeMs);
        }
    }
}