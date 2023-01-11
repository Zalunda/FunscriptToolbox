using CommandLine;
using FunscriptToolbox.Core;
using log4net;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerb
{
    class VerbSubtitlesSrt2WavChunks : VerbSubtitles
    {
        private static readonly ILog rs_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        [Verb("subtitles.srt2wavchunks", aliases: new[] { "sub.srt2wavs" }, HelpText = "Extract .srt, using Voice Activity Detection")]
        public class Options : OptionsBase
        {
            [Value(0, MetaName = "files", Required = true, HelpText = ".srt files")]
            public IEnumerable<string> Input { get; set; }

            [Option('r', "recursive", Required = false, HelpText = "If a file contains '*', allow to search recursivly for matches", Default = false)]
            public bool Recursive { get; set; }
        }

        private readonly Options r_options;

        public VerbSubtitlesSrt2WavChunks(Options options)
            : base(rs_log, options)
        {
            r_options = options;
        }

        public int Execute()
        {
            UpdateFfmpeg();

            foreach (var srtFullpath in r_options
                .Input
                .SelectMany(file => HandleStarAndRecusivity(file, r_options.Recursive))
                .Distinct()
                .OrderBy(f => f))
            {
                try
                {
                    var parentFolder = Path.GetDirectoryName(srtFullpath) ?? ".";
                    var baseOutput = Path.Combine(parentFolder, Path.GetFileNameWithoutExtension(srtFullpath));
                    var fileId = Path.GetFileName(srtFullpath).GetHashCode().ToString("X8");
                    var chunksFolder = baseOutput + $"_wav_chunks_{fileId}";

                    if (Directory.Exists(chunksFolder) && Directory.GetFiles(chunksFolder).Any())
                    {
                        WriteInfo($"{srtFullpath}: Skipping because folder '{chunksFolder}' already contains files. Please delete/rename the folder.", ConsoleColor.DarkGray);
                        continue;
                    }

                    var watch = Stopwatch.StartNew();

                    var srtFile = SubtitleFile.FromSrtFile(srtFullpath);
                    string wavFullpath = Path.ChangeExtension(srtFullpath, ".wav");

                    var fullPcmSamples = ConvertWavToPcmData(wavFullpath);

                    WriteInfo($"{srtFullpath}: Writing .wav files to folder {Path.GetFileName(chunksFolder)}...");

                    Directory.CreateDirectory(chunksFolder);
                    foreach (var subtitle in srtFile.Subtitles)
                    {
                        var duration = subtitle.EndTime - subtitle.StartTime;

                        var startIndex = GetNbSamplesFromDuration(subtitle.StartTime);
                        var durationIndex = GetNbSamplesFromDuration(duration);
                        var endIndex = startIndex + durationIndex;

                        ConvertPcmDataToWav(
                            Path.Combine(
                                chunksFolder,
                                $"__{fileId}__{(long)subtitle.StartTime.TotalMilliseconds:D7}_{(int)duration.TotalMilliseconds:D}__.wav"),
                            fullPcmSamples,
                            startIndex,
                            (endIndex > fullPcmSamples.Length)
                                ? fullPcmSamples.Length - startIndex
                                : durationIndex);
                    }

                    WriteInfo($"{srtFullpath}: Finished in {watch.Elapsed}.");
                }
                catch (Exception ex)
                {
                    WriteError($"{srtFullpath}: Exception occured: {ex}");
                }
            }

            return base.NbErrors;
        }

        private static int GetNbSamplesFromDuration(TimeSpan duration)
        {
            var number = (int)(duration.TotalSeconds * SamplingRate * NbBytesPerSampling);
            return (number % 2 == 0) ? number : number - 1;
        }
    }
}
