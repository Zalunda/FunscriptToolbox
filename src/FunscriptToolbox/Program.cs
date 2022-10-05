using CommandLine;
using FunscriptToolbox.AudioSyncVerbs;
using System;
using System.Collections.Generic;

namespace FunscriptToolbox
{
    class Program
    {
        static int HandleParseError(IEnumerable<Error> errs)
        {
            //handle errors
            return -1;
        }

        static int Main(string[] args)
        {
#if DEBUG
            int test = 2;

            switch (test)
            {
                case 1:
                    args = new[]
                    {
                        "audiosync.createaudiosignature"
                    };
                    break;
                case 2:
                    args = new[]
                    {
                        "audiosync.createaudiosignature",
                        "--force",
                        "*.mp4",
                        "*.funscript"
                    };
                    break;
                case 10:
                    args = new[]
                    {
                        "as.cfs"
                    };
                    break;
                case 11:
                    args = new[]
                    {
                        "as.cfs",
                        "-s", "NaughtyAmericaVR - 2017-04-20 - Melissa Moore - Wake and Bake [zalunda].funscript",
                        "-n", "mygfmelissaseth_vrdesktophd.asig",
                    };
                    break;
            }

#endif
            try
            {
                var result = Parser.Default.ParseArguments<
                    VerbAudioSyncCreateAudioSignature.Options,
                    VerbAudioSyncCreateFunscript.Options>(args)
                    .MapResult(
                          (VerbAudioSyncCreateAudioSignature.Options options) => new VerbAudioSyncCreateAudioSignature(options).Execute(),
                          (VerbAudioSyncCreateFunscript.Options options) => new VerbAudioSyncCreateFunscript(options).Execute(),
                          errors => HandleParseError(errors));
                return result;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                return -1;
            }
        }
    }
}
