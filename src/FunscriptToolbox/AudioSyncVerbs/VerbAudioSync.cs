using AudioSynchronization;
using FunscriptToolbox.Core;
using log4net;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.AudioSyncVerbs
{
    internal abstract class VerbAudioSync : Verb
    {
        protected const string NotesSynchronizedByFunscriptToolbox = "This is not the original funscript file. It's a synchronized version for a different video, done by " + ApplicationName + ".";
        private readonly string r_videoExtension;

        protected VerbAudioSync(ILog log, OptionsBase options, string videoExtension)
            : base(log, options)
        {
            r_videoExtension = videoExtension;
        }

        protected AudioSignatureWithLinkedFiles LoadAudioSignature(string id, string mainFilename)
        {
            WriteInfo($"{id} : {mainFilename}");
            return new AudioSignatureWithLinkedFiles(
                id,
                mainFilename,
                GetAudioSignature(mainFilename));
        }

        protected AudioSignatureWithLinkedFiles LoadAudioSignatureWithExtras(string id, string mainFilename)
        {
            var fileWithExtras = LoadAudioSignature(id, mainFilename);

            foreach (var funscriptFullName in Directory.GetFiles(fileWithExtras.BaseFolder, fileWithExtras.BaseFilename + "*.funscript"))
            {
                var suffixe = fileWithExtras.AddFunscriptFile(funscriptFullName, Funscript.FromFile(funscriptFullName));
                WriteInfo($"    Loading '{suffixe}' file...");
            }
            foreach (var srtFullName in Directory.GetFiles(fileWithExtras.BaseFolder, fileWithExtras.BaseFilename + "*.srt"))
            {
                var suffixe = fileWithExtras.AddSubtitleFile(srtFullName, SubtitleFile.FromSrtFile(srtFullName));
                WriteInfo($"    Loading '{suffixe}' file...");
            }
            return fileWithExtras;
        }

        protected AudioSignature GetAudioSignature(string filename)
        {
            var funscript = (string.Equals(Path.GetExtension(filename), Funscript.FunscriptExtension, StringComparison.OrdinalIgnoreCase))
                    ? Funscript.FromFile(filename)
                    : null;
            var asigFilename = Path.ChangeExtension(filename, Funscript.AudioSignatureExtension);
            if (funscript?.AudioSignature != null)
            {
                WriteInfo($"    Loading audio signature from '{Path.GetExtension(filename)}'...");
                return Convert(funscript.AudioSignature);
            }
            else if (File.Exists(asigFilename))
            {
                WriteInfo($"    Loading audio signature from '{Funscript.AudioSignatureExtension}'...");
                return Convert(Funscript.FromFile(asigFilename).AudioSignature);
            }
            else if (funscript != null)
            {
                WriteInfo($"    Extraction audio signature from '{r_videoExtension}'...");
                var videoFilename = Path.ChangeExtension(filename, r_videoExtension);
                return AudioTracksAnalyzer.ExtractSignature(videoFilename);
            }
            else
            {
                WriteInfo($"    Extraction audio signature from '{Path.GetExtension(filename)}'...");
                return AudioTracksAnalyzer.ExtractSignature(filename);
            }
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

        protected static string FormatTimeSpan(TimeSpan? value)
        {
            if (value == null)
                return string.Empty;
            return Regex.Replace(value.ToString(), @"\d{4}$", "");
        }
    }
}
