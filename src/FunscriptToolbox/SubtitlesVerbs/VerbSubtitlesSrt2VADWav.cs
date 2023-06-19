using AudioSynchronization;
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
    class VerbSubtitlesSrt2VADWav : VerbSubtitles
    {
        private static readonly ILog rs_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        [Verb("subtitles.srt2vadwav", aliases: new[] { "sub.srt2vadwav" }, HelpText = "Create temporary VADWAV for whipser")]
        public class Options : OptionsBase
        {
            [Value(0, MetaName = "files", Required = true, HelpText = ".srt files")]
            public IEnumerable<string> Input { get; set; }

            [Option('r', "recursive", Required = false, HelpText = "If a file contains '*', allow to search recursivly for matches", Default = false)]
            public bool Recursive { get; set; }

            [Option('f', "force", Required = false, HelpText = "Allow to force the execution", Default = false)]
            public bool Force { get; set; }

            [Option('s', "suffix", Required = false, HelpText = "Suffix for the files produced", Default = "")]
            public string Suffix { get; set; }
        }

        private readonly Options r_options;

        public VerbSubtitlesSrt2VADWav(Options options)
            : base(rs_log, options)
        {
            r_options = options;
        }

        public int Execute()
        {
            var silenceGapSamples = new byte[GetNbSamplesFromDuration(TimeSpan.FromSeconds(2))];

            foreach (var inputSrtFullpath in r_options
                .Input
                .SelectMany(file => HandleStarAndRecusivity(file, r_options.Recursive))
                .Distinct()
                .OrderBy(f => f))
            {
                var tempPcmFullpath = Path.GetTempFileName() + ".pcm";
                try
                {
                    var parentFolder = Path.GetDirectoryName(inputSrtFullpath) ?? ".";
                    var baseOutput = Path.Combine(parentFolder, Path.GetFileNameWithoutExtension(inputSrtFullpath));
                    var inputWavFullpath = Path.ChangeExtension(inputSrtFullpath, ".wav");
                    var outputWhisperWavFullpath = $"{baseOutput}{r_options.Suffix}.wav";
                    var outputWhisperOffsetFullpath = $"{baseOutput}{r_options.Suffix}.offset";

                    if (!r_options.Force && File.Exists(outputWhisperWavFullpath) && File.Exists(outputWhisperOffsetFullpath))
                    {
                        WriteInfo($"{inputSrtFullpath}: Skipping because file '{Path.GetFileName(outputWhisperWavFullpath)}' and '{Path.GetFileName(outputWhisperOffsetFullpath)}' already exists.  (use --force to override)", ConsoleColor.DarkGray);
                        continue;
                    }

                    var watch = Stopwatch.StartNew();

                    var inputPcmSamples = ConvertWavToPcmData(inputWavFullpath);

                    var inputSrtFile = SubtitleFile.FromSrtFile(inputSrtFullpath).Subtitles
                        .Where(f => f.Duration.TotalSeconds >= 0.5)
                        .ToArray();
                    var combinedDuration = TimeSpan.Zero;
                    var audioOffsets = new List<AudioOffset>();

                    var outputSrtFile = new SubtitleFile($"{baseOutput}{r_options.Suffix}.newvad.srt");

                    var nbBlock = 0;
                    var blockSize = TimeSpan.Zero;
                    var nbSubtitleInBloc = 0;
                    using (var pcmWriter = File.Create(tempPcmFullpath))
                    {
                        for (int index = 0; index < inputSrtFile.Length; index++)
                        {
                            var subtitle = inputSrtFile[index];
                            var nextSubtitle = index + 1 < inputSrtFile.Length ? inputSrtFile[index + 1] : null;

                            audioOffsets.Add(
                                new AudioOffset(
                                    combinedDuration,
                                    combinedDuration + (subtitle.EndTime - subtitle.StartTime),
                                    subtitle.StartTime - combinedDuration));
                            outputSrtFile.Subtitles.Add(
                                new Subtitle(
                                    combinedDuration,
                                    combinedDuration + (subtitle.EndTime - subtitle.StartTime),
                                    subtitle.Lines));

                            nbSubtitleInBloc++;
                            var duration = subtitle.EndTime - subtitle.StartTime;
                            var startIndex = GetNbSamplesFromDuration(subtitle.StartTime);
                            var durationIndex = GetNbSamplesFromDuration(duration);
                            var endIndex = startIndex + durationIndex;
                            pcmWriter.Write(
                                inputPcmSamples,
                                startIndex,
                                (endIndex > inputPcmSamples.Length) ? inputPcmSamples.Length - startIndex : endIndex - startIndex);
                            combinedDuration += duration;
                            blockSize += duration;

                            var gapDuration = TimeSpan.FromSeconds(0.3);
                            pcmWriter.Write(silenceGapSamples, 0, GetNbSamplesFromDuration(gapDuration));
                            combinedDuration += gapDuration;

                            nbBlock++;
                            blockSize = TimeSpan.Zero;
                            nbSubtitleInBloc = 0;
                        }
                    }

                    WriteInfo($"{inputSrtFullpath}: Writing .wav file for whisper ({inputSrtFile.Length} subtitles, {nbBlock} block, {combinedDuration})...");
                    ConvertPcmToWav(tempPcmFullpath, outputWhisperWavFullpath);

                    WriteInfo($"{inputSrtFullpath}: Writing .offset file...");
                    using (var writer = File.CreateText(outputWhisperOffsetFullpath))
                    {
                        Serializer.Serialize(writer, audioOffsets);
                    }

                    outputSrtFile.SaveSrt();

                    WriteInfo($"{inputSrtFullpath}: Finished in {watch.Elapsed}.");
                    WriteInfo();
                }
                catch (Exception ex)
                {
                    WriteError($"{inputSrtFullpath}: Exception occured: {ex}");
                    WriteInfo();
                }
                finally
                {
                    File.Delete(tempPcmFullpath);
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
