using CommandLine;
using log4net;
using System;
using System.Collections.Generic;

namespace FunscriptToolbox.MotionVectorsVerbs
{
    internal class VerbMotionVectorsPrepareFiles : VerbMotionVectors
    {
        private static readonly ILog rs_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        [Verb("motionvectors.prepare", aliases: new[] { "mvs.prep" }, HelpText = "Prepare a video for others motionvectors verb (i.e. create .mvs files)")]
        public class Options : OptionsBase
        {
            [Value(0, MetaName = "files", Required = false, HelpText = ".mp4 files")]
            public IEnumerable<string> Files { get; set; }

            [Option('r', "recursive", Required = false, HelpText = "If a file contains '*', allow to search recursivly for matches", Default = false)]
            public bool Recursive { get; set; }

            [Option('o', "outputfolder", Required = false, HelpText = "Folder to save the files. By default, they are saved in the same folder as the video", Default = null)]
            public string OutputFolder { get; set; }

            [Option('s', "suffix", Required = false, HelpText = "Suffix for the files produced", Default = "")]
            public string Suffix { get; set; }

            [Option('i', "additionalinputffmpeg", Required = false, HelpText = "Additional parameters for ffmpeg (ex. use hardware codec).", Default = "")]
            public string AdditionnalInputFfmpegParameters { get; set; }

            [Option('f', "ffmpegfilter", Required = false, HelpText = "Filter for ffmpeg. VRLeft, VRMosaic or 2D, or a 'real filter' like '-vf ...'", Default = "VRMosaic")]
            public string FfmpegFilter { get; set; }

            [Option('h', "ffmpegfilterHeight", Required = false, HelpText = "Only if using a named filter, the height of the produced video.", Default = "2048")]
            public string FfmpegFilterHeight { get; set; }

            [Option('v', "novisual", Required = false, HelpText = "Create a mvs-visual video, with only I-Frames", Default = false)]
            public bool DontCreateMvsVisualVideo { get; set; }

            [Option('p', "keeppframes", Required = false, HelpText = "Delete the .mvs-p-frames.mp4 file at the end of the process (it's not needed anymore)", Default = false)]
            public bool KeepPFramesMP4 { get; set; }
        }

        private readonly Options r_options;

        public VerbMotionVectorsPrepareFiles(Options options)
            : base(rs_log, options)
        {
            r_options = options;
        }

        public int Execute()
        {
            WriteInfo("Motion vectors preparation is no longer supported in FunscriptToolbox. You can still use the old version: https://github.com/Zalunda/FunscriptToolbox/releases/tag/2.2.0. To generate a video with embedded motion vector arrows, please use the custom FFmpeg build directly from: https://github.com/Zalunda/FFmpeg/releases", ConsoleColor.Yellow);
            return 0;
        }
    }
}
