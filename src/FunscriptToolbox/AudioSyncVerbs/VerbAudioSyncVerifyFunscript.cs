using AudioSynchronization;
using CommandLine;
using log4net;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.AudioSyncVerbs
{
    internal class VerbAudioSyncVerifyFunscript : VerbAudioSync
    {
        private static readonly ILog rs_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        [Verb("audiosync.verifyfunscript", aliases: new[] { "as.vfs" }, HelpText = "Verify a funscript.")]
        public class Options : OptionsBase
        {
            [Value(0, MetaName = "files", Required = true, HelpText = ".funscript files")]
            public IEnumerable<string> Files { get; set; }

            [Option('r', "recursive", Required = false, HelpText = "If a file contains '*', allow to search recursivly for matches", Default = false)]
            public bool Recursive { get; set; }

            [Option('f', "fix", Required = false, HelpText = "If a script is not synchronized, create a synchronized funscript", Default = false)]
            public bool FixSynchronization { get; set; }

            [Option('m', "minimumMatchLength", Required = false, HelpText = "Minimum match length, in second (used by the 'matching algorythm')", Default = 20)]
            public int MinimumMatchLength { get => m_minimumMatchLength; set => m_minimumMatchLength = ValidateMinValue(value, 5); }
            private int m_minimumMatchLength;

            [Option('p', "nbLocationsPerMinute", Required = false, HelpText = "Number of locations to try to match per minute of video (used by the 'matching algorythm')", Default = 10)]
            public int NbLocationsPerMinute { get => m_nbLocationsPerMinute; set => m_nbLocationsPerMinute = ValidateMinValue(value, 2); }
            private int m_nbLocationsPerMinute;

            [Option('e', "videoextension", Required = false, HelpText = "If a file is a funscript, use this extension to find the corresponding video", Default = ".mp4")]
            public string VideoExtension { get; set; }

            //[Usage(ApplicationAlias = "FunscriptToolBox")]
            //public static IEnumerable<Example> Examples
            //{
            //    get
            //    {
            //        yield return new Example(
            //            "Generate a matching funscript from a funscript containing an audio-signature",
            //            DefaultUnparserSettings,
            //            new Options { SourceFunscript = "original-with-audio-signature.funscript", NewAudio = "new-video-version.mp4" });
            //        yield return new Example(
            //            "Generate a funscript from a funscript and an audio signature.",
            //            DefaultUnparserSettings,
            //            new Options { SourceFunscript = "original-without-audio-signature.funscript", SourceAudio = "original.asig", NewAudio = "new-video-version.mp4" });
            //    }
            //}
        }

        private readonly Options r_options;

        public VerbAudioSyncVerifyFunscript(Options options)
            : base(rs_log, options)
        {
            r_options = options;
        }

        public int Execute()
        {
            foreach (var scripterFunscriptFilename in r_options
                .Files
                .SelectMany(file => HandleStarAndRecusivity(file, r_options.Recursive)))
            {
                var asigFilename = Path.ChangeExtension(scripterFunscriptFilename, Funscript.AudioSignatureExtension);
                var userVideoFilename = Path.ChangeExtension(scripterFunscriptFilename, r_options.VideoExtension);

                WriteInfo($"{scripterFunscriptFilename}: Loading scripter funscript...");
                var scripterFunscript = Funscript.FromFile(scripterFunscriptFilename);

                AudioSignature scripterAudioSignature;
                if (scripterFunscript.AudioSignature != null)
                {
                    WriteInfo($"{scripterFunscriptFilename}: Using audio signature from scripter funcript.");
                    scripterAudioSignature = scripterFunscript.AudioSignature;
                }
                else if (File.Exists(asigFilename))
                {
                    var scripterAsig = Funscript.FromFile(asigFilename);
                    WriteInfo($"{scripterFunscriptFilename}: Loading audio signature from scripter file '{asigFilename}'...");
                    scripterAudioSignature = scripterAsig.AudioSignature;
                }
                else
                {
                    scripterAudioSignature = null;
                }

                if (scripterAudioSignature == null)
                {
                    WriteInfo($"{scripterFunscriptFilename}: Skipping because there is no audio signature available for funscript.", ConsoleColor.Yellow);
                }
                else if (File.Exists(userVideoFilename))
                {
                    WriteInfo($"{scripterFunscriptFilename}: Extracting audio signature from video '{userVideoFilename}'...");
                    var userAudioSignature = ExtractAudioSignature(userVideoFilename);

                    WriteInfo($"{scripterFunscriptFilename}: Comparing audio signatures...");
                    SamplesComparer comparer = new SamplesComparer(
                                scripterAudioSignature,
                                userAudioSignature,
                                new CompareOptions
                                {
                                    MinimumMatchLength = TimeSpan.FromSeconds(r_options.MinimumMatchLength),
                                    NbLocationsPerMinute = r_options.NbLocationsPerMinute
                                });
                    var audioOffsets = comparer.FindAudioOffsets(WriteVerbose);

                    WriteInfo();
                    WriteInfo("Generating actions synchronized to the second audio signature...");
                    var newActions = TransformsActions(audioOffsets, scripterFunscript.Actions);

                    var audioSignatureMismatch = audioOffsets.Any(ao => ao.NbTimesUsed > 0 && ao.Offset != TimeSpan.Zero);
                    
                    if (audioSignatureMismatch)
                    {
                        if (r_options.FixSynchronization)
                        {
                            WriteInfo($"{scripterFunscriptFilename}: Audio signatures are NOT SYNCHRONIZED. Fixing it.");

                            var newFilename = scripterFunscriptFilename + ".original";
                            WriteInfo($"{scripterFunscriptFilename}: Renaming old funscript to '{newFilename}'.");
                            this.FunscriptVault.SaveFunscript(scripterFunscript, newFilename);

                            WriteInfo($"{scripterFunscriptFilename}: Creating synchronized version of the funscript.", ConsoleColor.Green);
                            scripterFunscript.Actions = newActions.ToArray();
                            scripterFunscript.AudioSignature = userAudioSignature;
                            scripterFunscript.AddNotes(NotesSynchronizedByFunscriptToolbox);
                            this.FunscriptVault.SaveFunscript(scripterFunscript, scripterFunscriptFilename);
                        }
                        else
                        {
                            WriteInfo($"{scripterFunscriptFilename}: Audio signatures are NOT SYNCHRONIZED. Script will not match.", ConsoleColor.Red);
                        }
                    }
                    else
                    {
                        WriteInfo($"{scripterFunscriptFilename}: Audio signatures are SYNCHRONIZED. Script is GOOD.", ConsoleColor.Green);
                    }
                }
                else
                {
                    WriteInfo($"{scripterFunscriptFilename}: Skipping because cannot find video file '{userVideoFilename}'.", ConsoleColor.Yellow);
                }
            }

            return 0;
        }
    }
}
