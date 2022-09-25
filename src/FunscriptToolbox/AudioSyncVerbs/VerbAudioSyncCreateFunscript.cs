using CommandLine;

namespace FunscriptToolbox.AudioSyncVerbs
{
    internal class VerbAudioSyncCreateFunscript : Verb
    {
        [Verb("audiosync.createFunscript", aliases: new[] { "as.cfs" })]
        public class Options : OptionsBase
        {
        }

        private readonly Options r_options;

        public VerbAudioSyncCreateFunscript(Options options)
            : base(options)
        {
            r_options = options;
        }

        public int Execute()
        {
            //handle options
            return 0;
        }
    }
}
