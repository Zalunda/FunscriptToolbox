using CommandLine;
using FunscriptToolbox.Core;
using log4net;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerb
{

    class VerbSubtitlesVideo2VADSrt : VerbSubtitles
    {
        private static readonly ILog rs_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        [Verb("subtitles.video2vadsrt", aliases: new[] { "sub.vid2vad" }, HelpText = "Extract an dummy .srt from video, using Voice Activity Detection")]
        public class Options : OptionsBase
        {
            [Value(0, MetaName = "files", Required = true, HelpText = ".mp4 files")]
            public IEnumerable<string> Input { get; set; }

            [Option('r', "recursive", Required = false, HelpText = "If a file contains '*', allow to search recursivly for matches", Default = false)]
            public bool Recursive { get; set; }

            [Option('f', "force", Required = false, HelpText = "Allow to force the execution", Default = false)]
            public bool Force { get; set; }

            [Option('o', "outputfolder", Required = false, HelpText = "Folder to save the files. By default, they are saved in the same folder as the video")]
            public string OutputFolder { get; set; }

            [Option('e', "baseextension", Required = false, HelpText = "Base file extension for the files produced", Default = ".temp.vad")]
            public string BaseExtension { get; set; }

            [Option('p', "extractionparameters", Required = false, HelpText = "Added parameters to pass to ffmpeg when extracting wav")]
            public string ExtractionParameters { get; set; }
        }

        private readonly Options r_options;

        public VerbSubtitlesVideo2VADSrt(Options options)
            : base(rs_log, options)
        {
            r_options = options;
        }

        public int Execute()
        {
            UpdateFfmpeg();

            foreach (var inputMp4Fullpath in r_options
                .Input
                .SelectMany(file => HandleStarAndRecusivity(file, r_options.Recursive))
                .Distinct()
                .OrderBy(f => f))
            {
                try
                {
                    var parentFolder = r_options.OutputFolder ?? Path.GetDirectoryName(inputMp4Fullpath) ?? ".";
                    var baseOutput = Path.Combine(parentFolder, Path.GetFileNameWithoutExtension(inputMp4Fullpath));
                    var outputSrtFullpath = $"{baseOutput}{r_options.BaseExtension}.srt";
                    var outputWavFullpath = $"{baseOutput}{r_options.BaseExtension}.wav";

                    if (!r_options.Force && File.Exists(outputSrtFullpath))
                    {
                        WriteInfo($"{inputMp4Fullpath}: Skipping because file '{Path.GetFileName(outputSrtFullpath)}' already  (use --force to override).");
                        continue;
                    }

                    var watch = Stopwatch.StartNew();

                    WriteInfo($"{inputMp4Fullpath}: Extracting .wav file from video...");
                    ConvertVideoToWav(inputMp4Fullpath, outputWavFullpath, r_options.ExtractionParameters);

                    WriteInfo($"{inputMp4Fullpath}: Extracting subtitles timing from .wav file, using Voice Activity Detection (silero-VAD)...");
                    var subtitles = ExtractSubtitleTimingWithVAD(outputWavFullpath).ToArray();

                    WriteInfo($"{inputMp4Fullpath}: {subtitles.Length} Voice Activities detected, writing vad subtitle...");
                    var emptySrt = new SubtitleFile(outputSrtFullpath, subtitles);
                    emptySrt.SaveSrt();

                    WriteInfo($"{inputMp4Fullpath}: Finished in {watch.Elapsed}.");
                    WriteInfo();
                }
                catch (Exception ex)
                {
                    WriteError($"{inputMp4Fullpath}: Exception occured: {ex}");
                    WriteInfo();
                }
            }

            return base.NbErrors;
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
                    var content = Serializer.Deserialize<dynamic>(jsonReader);

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
    }
}
