using CommandLine;
using log4net;

namespace FunscriptToolbox.MotionVectorsVerbs
{
    internal class VerbMotionVectorsUI : VerbMotionVectors
    {
        private static readonly ILog rs_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        [Verb("motionvectors.createfunscript", aliases: new[] { "mvs.cfs" }, HelpText = "TODO")]
        public class Options : OptionsBase
        {
            [Value(0, MetaName = "file", Required = true, HelpText = "TODO", Default = false)]
            public string file { get; set; }
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

            return 0;
        }
    }
}

