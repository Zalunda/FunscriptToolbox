using CommandLine;
using System;
using System.Collections.Generic;
using System.IO;

namespace FunscriptToolbox.AudioSyncVerbs
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
            var testFolder = @"C:\Partage\Medias\Adult\Videos VR\TODO\TestFunscriptToolbox";
            Environment.CurrentDirectory = testFolder;

            args = new[]
            {
                "audiosync.createAudioSignature",
                Path.Combine(testFolder, "*.mp4")
            };
            args = new[]
            {
                "audiosync.createAudioSignature",
                Path.Combine(testFolder, "*.funscript")
            };
            args = new[]
            {
                "audiosync.createAudioSignature",
                "*.mp4"
            };
            args = new[]
            {
                "as.cfs",
                "SalieriXXX - Lucy Li - Blowjob Competition [zalunda].ft.funscript",
                "SalieriXXX - Lucy Li - Blowjob Competition [zalunda].ft.funscript",
                "SalieriXXX - Lucy Li - Blowjob Competition (spankbang.com) [zalunda].mp4"
            };

#endif
            try
            {
                return Parser.Default.ParseArguments<
                    VerbAudioSyncCreateAudioSignature.Options,
                    VerbAudioSyncCreateFunscript.Options>(args)
                    .MapResult(
                          (VerbAudioSyncCreateAudioSignature.Options options) => new VerbAudioSyncCreateAudioSignature(options).Execute(),
                          (VerbAudioSyncCreateFunscript.Options options) => new VerbAudioSyncCreateFunscript(options).Execute(),
                          errors => HandleParseError(errors));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                return -1;
            }
        }
    }
}
