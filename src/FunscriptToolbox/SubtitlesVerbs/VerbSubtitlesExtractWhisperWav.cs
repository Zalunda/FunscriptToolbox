using CommandLine;
using log4net;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System;
using Xabe.FFmpeg;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using FunscriptToolbox.Core;
using System.Diagnostics;
using AudioSynchronization;

namespace FunscriptToolbox.SubtitlesVerb
{
    class VerbSubtitlesExtractWhisperWav : Verb
    {
        private static readonly ILog rs_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private const int SamplingRate = 16000;
        private const int NbBytesPerSampling = 2;

        [Verb("subtitles.extractwhisperwav", aliases: new[] { "sub.eww" }, HelpText = "Extract WAV optimized for whipser, using Voice Activity Detection")]
        public class Options : OptionsBase
        {
            [Value(0, MetaName = "files", Required = true, HelpText = ".mp4")]
            public IEnumerable<string> Input { get; set; }

            [Option('r', "recursive", Required = false, HelpText = "If a file contains '*', allow to search recursivly for matches", Default = false)]
            public bool Recursive { get; set; }

            [Option('g', "gapvad", HelpText = "Gap between the VAD added to the whisper .wav, in milliseconds", Default = 1000)]
            public int GapBetweenVAD { get; set; }

            [Option('f', "force", Required = false, HelpText = "Allow to redo existing extraction", Default = false)]
            public bool Force { get; set; }
        }

        private readonly static JsonSerializer rs_serializer = JsonSerializer
            .Create(new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                ContractResolver = new CamelCasePropertyNamesContractResolver()
            });

        private readonly Options r_options;

        public VerbSubtitlesExtractWhisperWav(Options options)
            : base(rs_log, options)
        {
            r_options = options;
        }

        public int Execute()
        {
            UpdateFfmpeg();

            var gapBetweenVAD = TimeSpan.FromMilliseconds(r_options.GapBetweenVAD);
            byte[] gapBetweenVADData = new byte[GetNbSamplesFromDuration(gapBetweenVAD)];

            foreach (var filepath in r_options
                .Input
                .SelectMany(file => HandleStarAndRecusivity(file, r_options.Recursive))
                .OrderBy(file => file))
            {
                var baseOuput = Path.Combine(
                    Path.GetDirectoryName(filepath) ?? ".",
                    Path.GetFileNameWithoutExtension(filepath));
                var wavWhisperFullpath = baseOuput + ".temp.whisper.wav";
                var wavOffsetWhisperFullpath = baseOuput + ".temp.whisper.offset";

                if (!r_options.Force && (File.Exists(wavWhisperFullpath) || File.Exists(wavOffsetWhisperFullpath)))
                {
                    WriteInfo($"{filepath}: Skipping because it already contains whisper files (use --force to override).");
                    continue;
                }

                var fullWavFile = Path.GetTempFileName() + ".full.wav";
                var tempPcmFile = Path.GetTempFileName() + ".raw";
                try
                {
                    var watch = Stopwatch.StartNew();

                    WriteInfo($"{filepath}: Extracting full .wav file...");
                    ExtractFullWav(filepath, fullWavFile);
                    var fullPcmSamples = ExtractPcmFromWav(fullWavFile);

                    WriteInfo($"{filepath}: Extracting subtitles timing from .wav file, using Voice Activity Detection (silero-VAD)...");
                    var subtitles = ExtractSubtitleTimingWithVAD(fullWavFile).ToArray();

                    WriteInfo($"{filepath}: Writing vad subtitle file with {subtitles.Length} VDA...");
                    var emptySrt = new SubtitleFile(baseOuput + ".temp.vad.srt", subtitles);
                    emptySrt.SaveSrt();

                    WriteInfo($"{filepath}: Writing .wav file for whisper (duration: {TimeSpan.FromMilliseconds(subtitles.Sum(f => f.Duration.TotalMilliseconds) + subtitles.Length * gapBetweenVAD.TotalMilliseconds)})...");
                    var combinedDuration = TimeSpan.Zero;
                    var audioOffsets = new List<AudioOffset>();

                    using (var pcmWriter = File.Create(tempPcmFile))
                    {
                        for (int index = 0; index < subtitles.Length; index++)
                        {
                            var subtitle = subtitles[index];
                            var duration = subtitle.EndTime - subtitle.StartTime;

                            audioOffsets.Add(
                                new AudioOffset(
                                    combinedDuration,
                                    combinedDuration + duration,
                                    subtitle.StartTime - combinedDuration));

                            var startIndex = GetNbSamplesFromDuration(subtitle.StartTime);
                            var durationIndex = GetNbSamplesFromDuration(duration);
                            var endIndex = startIndex + durationIndex;
                            pcmWriter.Write(
                                fullPcmSamples,
                                startIndex, 
                                (endIndex > fullPcmSamples.Length) ? fullPcmSamples.Length - startIndex : durationIndex);
                            combinedDuration += duration;

                            pcmWriter.Write(gapBetweenVADData, 0, gapBetweenVADData.Length);
                            combinedDuration += gapBetweenVAD;
                        }
                    }

                    SaveWavFile(tempPcmFile, wavWhisperFullpath);

                    using (var writer = File.CreateText(wavOffsetWhisperFullpath))
                    {
                        rs_serializer.Serialize(writer, audioOffsets);
                    }

                    WriteInfo($"{filepath}: Finished in {watch.Elapsed}.");
                }
                finally
                {
                    File.Delete(fullWavFile);
                }
            }

            return base.NbErrors;
        }

        private static int GetNbSamplesFromDuration(TimeSpan duration)
        {
            var number = (int)(duration.TotalSeconds * SamplingRate * NbBytesPerSampling);
            return (number % 2 == 0) ? number : number - 1;
        }

        private static void ExtractFullWav(string inputFilepath, string outputFilepath)
        {
            IMediaInfo mediaInfo = FFmpeg.GetMediaInfo(inputFilepath).GetAwaiter().GetResult();
            IStream audioStream = mediaInfo.AudioStreams.FirstOrDefault();
            FFmpeg.Conversions.New()
                .AddStream(audioStream)
                .SetOverwriteOutput(true)
                .AddParameter($"-acodec pcm_s16le -ac 1 -ar {SamplingRate}")
                .SetOutput(outputFilepath)
                .Start()
                .Wait();
        }

        private byte[] ExtractPcmFromWav(string inputFilepath)
        {
            var tempRawFile = Path.GetTempFileName() + ".pcm";
            try
            {
                IMediaInfo mediaInfo = FFmpeg.GetMediaInfo(inputFilepath).GetAwaiter().GetResult();
                IStream audioStream = mediaInfo.AudioStreams.FirstOrDefault();
                FFmpeg.Conversions.New()
                    .AddStream(audioStream)
                    .SetOverwriteOutput(true)
                    .AddParameter($"-f s16le -acodec pcm_s16le -ac 1 -ar {SamplingRate}")
                    .SetOutput(tempRawFile)
                    .Start()
                    .Wait();

                return File.ReadAllBytes(tempRawFile);
            }
            finally
            {
                File.Delete(tempRawFile);
            }
        }

        private IEnumerable<Subtitle> ExtractSubtitleTimingWithVAD(string inputFilepath)
        {
            var tempVadFilepath = Path.GetTempFileName() + ".vad.json";
            try
            {
                Process process = new Process();
                var scriptMessages = new List<string>();
                try
                {
                    var pythonScript = GetApplicationFolder(@"PythonSource\funscripttoolbox-extract-vad.py");

                    // Start the process
                    process.StartInfo.FileName = "Python.exe";
                    process.StartInfo.Arguments = $"{pythonScript} \"{inputFilepath}\" \"{tempVadFilepath}\" {SamplingRate}";
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.RedirectStandardError = true;
                    process.OutputDataReceived += (sender, e) => {
                        scriptMessages.Add(e.Data);
                        WriteVerbose($"   [Silerio-VAD O] {e.Data}");
                        };
                    process.ErrorDataReceived += (sender, e) => {
                        scriptMessages.Add(e.Data);
                        WriteVerbose($"   [Silerio-VAD E] {e.Data}");
                    };
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    process.WaitForExit();
                    if (process.ExitCode != 0)
                    {
                        throw new ApplicationException($"{pythonScript} returned code: {process.ExitCode}");
                    }
                }
                catch (Exception)
                {
                    WriteError($"An exception occured while running the following python script: {process.StartInfo.FileName} {process.StartInfo.Arguments}");
                    WriteError($"    Make sure that 'Python' is installed (https://www.python.org/downloads/).");
                    WriteError($"    If python is installed, make sure to run the following command in a command prompt: pip install pytorch torchaudio IPython");
                    WriteError();
                    if (scriptMessages.Count > 0)
                    {
                        WriteError("Script output:");
                        foreach (var message in scriptMessages)
                        {
                            WriteError($"   {message}");
                        }
                        WriteError();
                    }
                    throw;
                }

                // Read the file produced
                using (var reader = File.OpenText(tempVadFilepath))
                using (var jsonReader = new JsonTextReader(reader))
                {
                    var content = rs_serializer.Deserialize<dynamic>(jsonReader);

                    foreach (var item in content)
                    {
                        var start = (double)item.start / SamplingRate;
                        var end = (double)item.end / SamplingRate;
                        yield return new Subtitle(
                                TimeSpan.FromSeconds(start),
                                TimeSpan.FromSeconds(end),
                                ".");
                    }
                }
            }
            finally
            {
                File.Delete(tempVadFilepath);
            }
        }

        private void SaveWavFile(string inputPcmFilepath, string outputWavFilepath)
        {
            FFmpeg.Conversions.New()
                .SetOverwriteOutput(true)
                .AddParameter($"-f s16le -ar 16000 -ac 1 -i \"{inputPcmFilepath}\" -acodec pcm_s16le")
                .SetOutput(outputWavFilepath)
                .Start()
                .Wait();
        }
    }
}
