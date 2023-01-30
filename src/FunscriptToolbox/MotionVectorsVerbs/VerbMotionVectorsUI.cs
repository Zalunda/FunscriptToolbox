using CommandLine;
using FunscriptToolbox.UI;
using log4net;
using System;

namespace FunscriptToolbox.MotionVectorsVerbs
{
    internal class VerbMotionVectorsUI : VerbMotionVectors
    {
        private static readonly ILog rs_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        [Verb("motionvectors.ui", aliases: new[] { "mvs.ui" }, HelpText = "TODO")]
        public class Options : OptionsBase
        {
            [Value(0, MetaName = "file", Required = true, HelpText = "TODO")]
            public string file { get; set; }

            [Option('i', "inputparametersfile", Required = true, HelpText = "TODO")]
            public string InputParametersFile { get; set; }

            [Option('o', "outputparametersfile", Required = true, HelpText = "TODO")]
            public string OutputParametersFile { get; set; }
        }

        private readonly Options r_options;

        public VerbMotionVectorsUI(Options options)
            : base(rs_log, options)
        {
            r_options = options;
        }

        public int Execute()
        {
            UpdateFfmpeg();

            Test.TestUI(r_options.file, r_options.InputParametersFile, r_options.OutputParametersFile);

            return 0;
        }
    }
}

