using CommandLine;
using log4net;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Xabe.FFmpeg;

namespace FunscriptToolbox.MotionVectorsVerbs
{
    internal class VerbMotionVectorsPrepareFiles : VerbMotionVectors
    {
        private static readonly ILog rs_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        [Verb("motionvectors.prepare", aliases: new[] { "mvs.prep" }, HelpText = "Prepare a video for others motionvectors verb (i.e. create .mvs files)")]
        public class Options : OptionsBase
        {
            [Value(0, MetaName = "files", Required = true, HelpText = ".mp4 files")]
            public IEnumerable<string> Files { get; set; }

            [Option('r', "recursive", Required = false, HelpText = "If a file contains '*', allow to search recursivly for matches", Default = false)]
            public bool Recursive { get; set; }

            [Option('o', "outputfolder", Required = false, HelpText = "Folder to save the files. By default, they are saved in the same folder as the video", Default = null)]
            public string OutputFolder { get; set; }

            [Option('s', "suffix", Required = false, HelpText = "Suffix for the files produced", Default = "")]
            public string Suffix { get; set; }

            [Option('p', "keepp", Required = false, HelpText = "Keep the .mvs-p-frames.mp4 file at the end of the process (it's not needed anymore)", Default = true)]
            public bool KeepPFramesMP4 { get; set; }

            [Option('v', "visual", Required = false, HelpText = "Keep/Need a mvs-visual video, with only I-Frames", Default = true)]
            public bool KeepMvsVisualVideo { get; set; }

            [Option('i', "additionalinputffmpeg", Required = false, HelpText = "Additional parameters for ffmpeg (ex. use hardware codec).", Default = "")]
            public string AdditionnalInputFfmpegParameters { get; set; }

            [Option('f', "ffmpegfilter", Required = false, HelpText = "Filter for ffmpeg. VR1, VR2 or 2D, or a 'real filter' like '-vf ...'", Default = "VR2")]
            public string FfmpegFilter { get; set; }

            [Option('h', "ffmpegfilterHeight", Required = false, HelpText = "Only if using a named filter, the height of the produced video.", Default = "2048")]
            public string FfmpegFilterHeight { get; set; }
        }

        private static readonly Dictionary<string, string> rs_namedFFmpegFilter = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "VR1", "-filter:v \"crop=in_w/2:in_h:0:0,scale=-1:{HEIGHT}\"" },
                { "VR2", "-filter_complex \"[0:v]crop=in_w/2:in_h:0:0,scale=-1:{HEIGHT}[A];[0:v]v360=input=he:in_stereo=sbs:pitch=-20:yaw=0:roll=0:output=flat:d_fov=90:w={HEIGHT}/2:h={HEIGHT}/2[B1];[0:v]v360=input=he:in_stereo=sbs:pitch=-55:yaw=0:roll=0:output=flat:d_fov=90:w={HEIGHT}/2:h={HEIGHT}/2[B2];[B1][B2]vstack=inputs=2[B];[A][B]hstack=inputs=2\"" },
                { "2D", "-filter:v \"scale=-1:{HEIGHT}\"" },
            };

        private readonly Options r_options;

        public VerbMotionVectorsPrepareFiles(Options options)
            : base(rs_log, options)
        {
            r_options = options;
        }

        public int Execute()
        {
            foreach (var inputMp4FullPath in r_options
                .Files
                .SelectMany(file => HandleStarAndRecusivity(file, r_options.Recursive))
                .Where(f => !f.Contains(".mvs")))
            {
                var parentFolder = r_options.OutputFolder ?? Path.GetDirectoryName(inputMp4FullPath) ?? ".";
                var baseOutputPath = Path.Combine(parentFolder, Path.GetFileNameWithoutExtension(inputMp4FullPath));

                var outputPFramesMp4FullPath = $"{baseOutputPath}{r_options.Suffix}.mvs-p-frames.mp4";
                var outputMvsFullPath = $"{baseOutputPath}{r_options.Suffix}.mvs";
                var outputVisualMvsMp4FullPath = $"{baseOutputPath}{r_options.Suffix}.mvs-visual.mp4";

                if (File.Exists(outputPFramesMp4FullPath))
                {
                    WriteInfo($"{inputMp4FullPath}: Skipping creating a motion vectors optimized video (i.e. p-frames only file) because '{Path.GetFileName(outputPFramesMp4FullPath)}' already exists.", ConsoleColor.DarkGray);
                }
                else if (r_options.KeepPFramesMP4 || !File.Exists(outputMvsFullPath) || (r_options.KeepMvsVisualVideo && !File.Exists(outputMvsFullPath)))
                {
                    WriteInfo($"{inputMp4FullPath}: Creating a motion vectors optimized video (i.e. p-frames only file) '{Path.GetFileName(outputPFramesMp4FullPath)}'");
                    if (rs_namedFFmpegFilter.TryGetValue(r_options.FfmpegFilter, out var ffmpegFilter))
                    {
                        int.TryParse(r_options.FfmpegFilterHeight, out var height);
                        if (height % 16 != 0)
                        {
                            throw new Exception($"Height ({r_options.FfmpegFilterHeight}) need to be divisible by 16.");
                        }
                        ffmpegFilter = ffmpegFilter.Replace("{HEIGHT}", height.ToString());
                        WriteInfo($"{inputMp4FullPath}:     using named filter '{r_options.FfmpegFilter}': {ffmpegFilter}...");
                    }
                    else
                    {
                        ffmpegFilter = r_options.FfmpegFilter;
                        WriteInfo($"{inputMp4FullPath}:     using provied filter: {ffmpegFilter}...");
                    }

                    var stopWatch = Stopwatch.StartNew();
                    CreatePFrameMP4(inputMp4FullPath, outputPFramesMp4FullPath, ffmpegFilter);
                    WriteInfo($"{inputMp4FullPath}: Done in {stopWatch.Elapsed}.");
                    WriteInfo();
                }
                else
                {
                    WriteInfo($"{inputMp4FullPath}: Skipping creating a motion vectors optimized video (i.e. p-frames only file) because '{Path.GetFileName(outputPFramesMp4FullPath)}' is not needed.", ConsoleColor.DarkGray);
                }

                if (File.Exists(outputMvsFullPath))
                {
                    WriteInfo($"{inputMp4FullPath}: Skipping creating a motion vectors file because  '{Path.GetFileName(outputMvsFullPath)}' it already exists.", ConsoleColor.DarkGray);
                }
                else
                {
                    WriteInfo($"{inputMp4FullPath}: Creating a motion vectors file '{Path.GetFileName(outputMvsFullPath)}'...");
                    var stopWatch = Stopwatch.StartNew();
                    CreateMvsFile(outputPFramesMp4FullPath, outputMvsFullPath);
                    WriteInfo($"{inputMp4FullPath}: Done in {stopWatch.Elapsed}.");
                    WriteInfo();
                }

                if (File.Exists(outputVisualMvsMp4FullPath))
                {
                    WriteInfo($"{inputMp4FullPath}: Skipping creating a motion vectors optimized video (i.e. only i-frames with motion vectors baked-in) because '{Path.GetFileName(outputVisualMvsMp4FullPath)}' already exists.", ConsoleColor.DarkGray);
                }
                else if (r_options.KeepMvsVisualVideo)
                {
                    WriteInfo($"{inputMp4FullPath}: Creating a video with visual motion vector '{Path.GetFileName(outputVisualMvsMp4FullPath)}'...");
                    var stopWatch = Stopwatch.StartNew();
                    CreateMP4WithVisualMotionVector(outputPFramesMp4FullPath, outputVisualMvsMp4FullPath);
                    WriteInfo($"{inputMp4FullPath}: Done in {stopWatch.Elapsed}.");
                    WriteInfo();
                }
                else
                {
                    WriteInfo($"{inputMp4FullPath}: Skipping creating a motion vectors optimized video (i.e. only i-frames with motion vectors baked-in) because '{Path.GetFileName(outputVisualMvsMp4FullPath)}' is not needed.", ConsoleColor.DarkGray);
                }

                if (!r_options.KeepPFramesMP4)
                {
                    WriteInfo($"{inputMp4FullPath}: Now that the .msv file is created, deleting motion vectors optimized video (i.e. p-frames only file) '{Path.GetFileName(outputPFramesMp4FullPath)}'.");
                    File.Delete(outputPFramesMp4FullPath);
                }
            }
            return 0;
        }

        private void CreatePFrameMP4(string mp4FullPath, string outputPFramesMp4FullPath, string ffmpegFilter)
        {
            var conversion = FFmpeg.Conversions.New()
                .SetOverwriteOutput(true)
                .AddParameter($"{r_options.AdditionnalInputFfmpegParameters.Replace("'", "")} -i \"{mp4FullPath}\" {ffmpegFilter} -bf 0 -g 100000 -c:a copy")
                .SetOutput(outputPFramesMp4FullPath);
            StartAndHandleFfmpegProgress(conversion);
        }

        private void CreateMvsFile(string outputPFramesMp4FullPath, string outputMvsFullPath)
        {
            try
            {
                var process = new Process();
                process.StartInfo.FileName = "MotionVectorsExtractor.exe";
                process.StartInfo.Arguments = $"\"{outputPFramesMp4FullPath}\" \"{outputMvsFullPath}\"";
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.UseShellExecute = false;

                var stopwatch = Stopwatch.StartNew();
                TimeSpan total = TimeSpan.Zero;
                DateTime nextUpdate = DateTime.MinValue;
                var errors = new List<string>();
                process.ErrorDataReceived += (s, e) => errors.Add(e.Data);
                process.OutputDataReceived += (s, e) => {
                    if (DateTime.Now > nextUpdate && e.Data != null)
                    {
                        var line = e.Data;
                        var split = line.Split(',');
                        if (split.Length > 4)
                        {
                            var duration = TimeSpan.FromMilliseconds(int.Parse(split[2]));
                            total = TimeSpan.FromMilliseconds(int.Parse(split[3]));
                            var percent = duration.TotalSeconds / total.TotalSeconds * 100;
                            if (percent > 0)
                            {
                                var timeLeft = TimeSpan.FromMilliseconds(stopwatch.ElapsedMilliseconds / percent * (100 - percent));
                                WriteInfo($"[MotionVectorsExtractor]   [{duration} / {total}] {(int)(Math.Round(percent, 2))}% => elapsed : {stopwatch.Elapsed} left: {timeLeft}", isProgress: true);
                            }
                        }
                        nextUpdate = DateTime.Now + TimeSpan.FromSeconds(1);
                    }
                    WriteDebug(e.Data);
                };
                process.Start();
                process.BeginErrorReadLine();
                process.BeginOutputReadLine();
                process.WaitForExit();

                WriteInfo($"[MotionVectorsExtractor]   Handling a video of {total} took {stopwatch.Elapsed}.");
            }
            catch
            {
                File.Delete(outputMvsFullPath);
            }
        }

        private void CreateMP4WithVisualMotionVector(string sourceFullPath, string destinationFullPath)
        {
            var conversion = FFmpeg.Conversions.New()
                .SetOverwriteOutput(true)
                .AddParameter($"-flags2 +export_mvs -i \"{sourceFullPath}\" -filter:v codecview=mv=pf+bf+bb -g 1 -c:a copy")
                .SetOutput(destinationFullPath);
            StartAndHandleFfmpegProgress(conversion);
        }
    }
}
