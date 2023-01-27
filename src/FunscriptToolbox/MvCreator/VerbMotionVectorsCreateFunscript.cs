using CommandLine;
using FunscriptToolbox.Core;
using log4net;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.MotionVectorsVerbs
{
    internal partial class VerbMotionVectorsCreateFunscript : VerbMotionVectors
    {
        private static readonly ILog rs_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        [Verb("motionvectors.createfunscript", aliases: new[] { "mvs.cfs" }, HelpText = "Create draft funscript from the motions vector")]
        public class Options : OptionsBase
        {
            [Value(0, MetaName = "files", Required = true, HelpText = ".mvs files")]
            public IEnumerable<string> Files { get; set; }

            [Option('r', "recursive", Required = false, HelpText = "If a file contains '*', allow to search recursivly for matches", Default = false)]
            public bool Recursive { get; set; }

            [Option('f', "force", Required = false, HelpText = "If a funscript already contains an audio signature, allow to replace it", Default = false)]
            public bool Force { get; set; }
        }

        private readonly Options r_options;

        public VerbMotionVectorsCreateFunscript(Options options)
            : base(rs_log, options)
        {
            r_options = options;
        }

        public int Execute()
        {
            UpdateFfmpeg();

            foreach (var file in r_options
                .Files
                .SelectMany(file => HandleStarAndRecusivity(file, r_options.Recursive)))
            {
                var outputFunscript = Path.ChangeExtension(file, ".TestA.funscript");

                if (!r_options.Force && File.Exists(outputFunscript))
                {
                    WriteInfo($"{file}: Skipping because '{Path.GetFileName(outputFunscript)}' already exists (use --force to override).", ConsoleColor.DarkGray);
                    continue;
                }

                var reader = new MotionVectorsFileReader(file);
                var frameAnalysers = new FrameAnalyser[]
                    {
                        // new FrameAnalyserSimpleNumberOfUpOrDownMotionX(),
                        //new FrameAnalyserSimpleNumberOfUpOrDownMotionY(),
                        // new FrameAnalyserSimpleSumMotionX(),
                        new FrameAnalyserSimpleSumMotionY("SumMotionY", reader.NbBlocX, reader.NbBlocY, 0, 0, 128, 128),
                        new FrameAnalyserSimpleSumMotionY("SumMotionY-WomenMiddle", reader.NbBlocX, reader.NbBlocY, 44, 28, 30, 60),
                        new FrameAnalyserSimpleSumMotionY("SumMotionY-MenBottom", reader.NbBlocX, reader.NbBlocY,(128-14)/2, 108, 14, 20),
                        new FrameAnalyserSimpleSumMotionY("SumMotionY-MenBottomFOV", reader.NbBlocX, reader.NbBlocY,128 + 15, 96, 64 - 30, 32)
                    };

                var stopwatch = Stopwatch.StartNew();
                foreach (var frame in reader.ReadFrames())
                {
                    foreach (var analyser in frameAnalysers)
                    {
                        analyser.AddFrameData(frame);
                    }
                }
                stopwatch.Stop();
                WriteInfo($"{file}: File read in {stopwatch.Elapsed}.");

                foreach (var analyser in frameAnalysers)
                {
                    var funscriptFullpath = Path.ChangeExtension(file, $".mvs-visual.{analyser.Name}.funscript");
                    var funscript = new Funscript();
                    funscript.Actions = analyser.Actions.ToArray();
                    funscript.Save(funscriptFullpath);
                }
            }
            return 0;
        }
    }
}
