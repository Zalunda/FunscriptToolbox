using AudioSynchronization;
using FunscriptToolbox.Core;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.AudioSyncVerbs
{
    internal class VirtualMergedFile
    {
        public VirtualMergedFile(string id, IEnumerable<AudioSignatureWithLinkedFiles> files)
        {
            this.Id = id;
            this.Files = files.ToArray();

            var nbSamplesPerSecond = 0;
            var samples = new List<ushort>();
            var currentTime = TimeSpan.Zero;
            foreach (var file in this.Files)
            {
                if (nbSamplesPerSecond == 0)
                {
                    nbSamplesPerSecond = file.AudioSignature.NbSamplesPerSecond;
                }
                else if (nbSamplesPerSecond != file.AudioSignature.NbSamplesPerSecond)
                {
                    throw new ArgumentException($"File '{file.Id}' has a NbSamplesPerSecond {file.AudioSignature.NbSamplesPerSecond} while first file had {nbSamplesPerSecond}. They should all have the same value.");
                }

                samples.AddRange(file.AudioSignature.UncompressedSamples);
                file.StartTime = currentTime;
                currentTime += file.Duration;
            }
            MergedAudioSignature = AudioSignature.FromSamples(nbSamplesPerSecond, samples.ToArray());
        }

        public string Id { get; }
        public AudioSignatureWithLinkedFiles[] Files { get; private set; }
        public AudioSignature MergedAudioSignature { get; internal set; }

        public Dictionary<string, Funscript> MergedFunscriptFiles
        {
            get
            {
                var result = new Dictionary<string, Funscript>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in this.Files)
                {
                    int fileStartAt = (int)file.StartTime.TotalMilliseconds;
                    int fileDurationInMs = (int)file.Duration.TotalMilliseconds;
                    foreach (var item in file.Funscripts)
                    {
                        if (!result.TryGetValue(item.Key, out var funscript))
                        {
                            funscript = item.Value.CloneWithoutActionsOrChapters();
                            result.Add(item.Key, funscript);
                        }

                        foreach (var action in item.Value.Actions)
                        {
                            if (action.At >= 0 && action.At < fileDurationInMs) // In case the funscript action 'overlap' with another file
                            {
                                funscript.AddActionDelayed(
                                    new FunscriptAction(
                                        fileStartAt + action.At,
                                        action.Pos));
                            }
                        }
                        foreach (var chapter in item.Value.GetClonedChapters())
                        {
                            var startTime = Funscript.FromChapterTime(chapter.startTime);
                            if (startTime >= TimeSpan.Zero && startTime <= file.Duration) // In case the chapter 'overlap' with another file
                            {
                                chapter.startTime = Funscript.ToChapterTime(file.StartTime + startTime);
                                chapter.endTime = Funscript.ToChapterTime(file.StartTime + Funscript.FromChapterTime(chapter.endTime));
                                funscript.AddChapterDelayed(chapter);
                            }
                        }
                    }
                }
                return result;
            }
        }

        public Dictionary<string, SubtitleFile> MergedSubtitleFiles
        {
            get
            {
                var result = new Dictionary<string, SubtitleFile>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in this.Files)
                {
                    foreach (var item in file.Subtitles)
                    {
                        if (!result.TryGetValue(item.Key, out var subtitleFile))
                        {
                            subtitleFile = new SubtitleFile();
                            result.Add(item.Key, subtitleFile);
                        }

                        foreach (var subtitle in item.Value.Subtitles)
                        {
                            if (subtitle.StartTime >= TimeSpan.Zero && subtitle.EndTime <= file.Duration) // In case the subtitles 'overlap' with another file
                            {
                                subtitleFile.Subtitles.Add(
                                    new Subtitle(
                                        file.StartTime + subtitle.StartTime,
                                        file.StartTime + subtitle.EndTime,
                                        subtitle.Lines));
                            }
                        }
                    }
                }
                return result;
            }
        }
        internal void AddFunscriptFile(
            string suffixe,
            Funscript funscript,
            Func<ItemType, TimeSpan, TimeSpan, IEnumerable<TransformedTimeRange>> transformFunc)
        {
            foreach (var action in funscript.Actions)
            {
                var time = TimeSpan.FromMilliseconds(action.At);
                foreach (var transform in transformFunc(ItemType.Actions, time, time))
                {
                    transform.OutputFile.AddAction(
                        suffixe,
                        funscript,
                        new FunscriptAction(
                            (int)transform.RelativeStartTime.TotalMilliseconds,
                            action.Pos));
                }
            }

            foreach (var chapter in funscript.GetClonedChapters())
            {
                var startTime = Funscript.FromChapterTime(chapter.startTime);
                var endTime = Funscript.FromChapterTime(chapter.endTime);

                foreach (var transform in transformFunc(ItemType.Chapters, startTime, endTime))
                {
                    var newChapter = chapter.DeepClone();
                    newChapter.startTime = Funscript.ToChapterTime(transform.RelativeStartTime);
                    newChapter.endTime = Funscript.ToChapterTime(transform.RelativeEndTime);

                    transform.OutputFile.AddChapter(suffixe, funscript, newChapter);
                }
            }
        }

        internal void AddSubtitleFile(
            string suffixe,
            SubtitleFile subtitleFile,
            Func<ItemType, TimeSpan, TimeSpan, IEnumerable<TransformedTimeRange>> transformFunc)
        {
            foreach (var subtitle in subtitleFile.Subtitles)
            {
                foreach (var transform in transformFunc(ItemType.Subtitles, subtitle.StartTime, subtitle.EndTime))
                {
                    transform.OutputFile.AddSubtitle(
                        suffixe,
                        new Subtitle(
                            transform.RelativeStartTime,
                            transform.RelativeEndTime,
                            subtitle.Lines));
                }
            }
        }
    }
}
