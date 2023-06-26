using AudioSynchronization;
using FunscriptToolbox.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.AudioSyncVerbs
{
    internal class AudioSignatureWithLinkedFiles
    {    
        public AudioSignatureWithLinkedFiles(
            string id, 
            string baseFileName, 
            AudioSignature audioSignature)
        {
            this.Id = id;
            this.StartTime = TimeSpan.Zero;
            this.BaseFolder = Path.GetDirectoryName(Path.GetFullPath(baseFileName));
            this.BaseFilename = Path.GetFileNameWithoutExtension(baseFileName);
            this.BaseFullPath = Path.Combine(BaseFolder, BaseFilename);
            this.AudioSignature = audioSignature;
            this.Funscripts = new Dictionary<string, Funscript>(StringComparer.OrdinalIgnoreCase);
            this.Subtitles = new Dictionary<string, SubtitleFile>(StringComparer.OrdinalIgnoreCase);
        }

        public string Id { get; }
        public TimeSpan StartTime { get; set; }
        public string BaseFolder { get; }
        public string BaseFilename { get; }
        public string BaseFullPath { get; }
        public AudioSignature AudioSignature { get; }
        public TimeSpan Duration => this.AudioSignature.Duration;

        public Dictionary<string, Funscript> Funscripts { get; }
        public Dictionary<string, SubtitleFile> Subtitles { get; }

        public string AddFunscriptFile(string filename, Funscript funscript)
        {
            var suffixe = GetSuffixe(filename);
            this.Funscripts[suffixe] = funscript;
            return suffixe;
        }

        public string AddSubtitleFile(string filename, SubtitleFile subtitle)
        {
            var suffixe = GetSuffixe(filename);
            this.Subtitles[suffixe] = subtitle;
            return suffixe;
        }

        private string GetSuffixe(string filename)
        {
            var fullpath = Path.GetFullPath(filename);
            if (fullpath.StartsWith(this.BaseFullPath, StringComparison.OrdinalIgnoreCase))
            {
                return fullpath.Substring(this.BaseFullPath.Length);
            }
            else
            {
                throw new Exception($"Filename '{fullpath}' does not start with the base filename '{this.BaseFullPath}'.");
            }
        }

        internal void AddAction(string suffixe, Funscript source, FunscriptAction action)
        {
            if (!this.Funscripts.TryGetValue(suffixe, out var funscript))
            {
                funscript = source.CloneWithoutActionsOrChapters();
                this.Funscripts[suffixe] = funscript;
            }

            funscript.AddActionDelayed(action);
        }

        internal void AddChapter(string suffixe, Funscript source, dynamic chapter)
        {
            if (!this.Funscripts.TryGetValue(suffixe, out var funscript))
            {
                funscript = source.CloneWithoutActionsOrChapters();
                this.Funscripts[suffixe] = funscript;
            }

            funscript.AddChapterDelayed(chapter);
        }

        internal void AddSubtitle(string suffixe, Subtitle subtitle)
        {
            if (!this.Subtitles.TryGetValue(suffixe, out var subtitleFile))
            {
                subtitleFile = new SubtitleFile();
                this.Subtitles[suffixe] = subtitleFile;
            }

            subtitleFile.Subtitles.Add(subtitle);
        }
    }
}
