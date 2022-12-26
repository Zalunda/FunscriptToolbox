using AudioSynchronization;
using CommandLine;
using CommandLine.Text;
using FunscriptToolbox.Core;
using log4net;
using System;
using System.Collections.Generic;
using System.IO;

namespace FunscriptToolbox.AudioSyncVerbs
{
    internal class VerbAudioSyncCreateFunscript : VerbAudioSync
    {
        private static readonly ILog rs_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        [Verb("audiosync.createfunscript", aliases: new[] { "as.cfs" }, HelpText = "Take an audio signature and funscript and try to generate a funscript synchronized to a different videos.")]
        public class Options : OptionsBase
        {
            [Option('s', "source", Required = true, HelpText = "original .funscript file")]
            public string SourceFunscript { get; set; }

            [Option('a', "sourceAudio", HelpText = "original .mp4 or .asig file")]
            public string SourceAudio { get; set; }

            [Option('n', "newAudio", Required = true, HelpText = "new .mp4 or .asig file")]
            public string NewAudio { get; set; }

            [Option('o', "outputFunscript", HelpText = "(Default: <newaudio-without-extension>.funscript) path to the generated funscript")]
            public string OutputFunscript { get; set; }

            [Option('m', "minimumMatchLength", Required = false, HelpText = "Minimum match length, in second (used by the 'matching algorythm')", Default = 20)]
            public int MinimumMatchLength { get => m_minimumMatchLength; set => m_minimumMatchLength = ValidateMinValue(value, 5); }
            private int m_minimumMatchLength;

            [Option('p', "nbLocationsPerMinute", Required = false, HelpText = "Number of locations to try to match per minute of video (used by the 'matching algorythm')", Default = 10)]
            public int NbLocationsPerMinute { get => m_nbLocationsPerMinute; set => m_nbLocationsPerMinute = ValidateMinValue(value, 2); }
            private int m_nbLocationsPerMinute;

            [Option('e', "videoextension", Required = false, HelpText = "If a file is a funscript, use this extension to find the corresponding video", Default = ".mp4")]
            public string VideoExtension { get; set; }

            [Usage(ApplicationAlias = Verb.ApplicationName)]
            public static IEnumerable<Example> Examples
            {
                get
                {
                    yield return new Example(
                        "Generate a matching funscript from a funscript containing an audio-signature",
                        DefaultUnparserSettings,
                        new Options { SourceFunscript = "original-with-audio-signature.funscript", NewAudio = "new-video-version.mp4" });
                    yield return new Example(
                        "Generate a funscript from a funscript and an audio signature.",
                        DefaultUnparserSettings,
                        new Options { SourceFunscript = "original-without-audio-signature.funscript", SourceAudio = "original.asig", NewAudio = "new-video-version.mp4" });
                }
            }
        }

        private readonly Options r_options;

        public VerbAudioSyncCreateFunscript(Options options)
            : base(rs_log, options)
        {
            r_options = options;
        }

        private AudioSignature GetAudioSignature(string filename)
        {
            if (string.Equals(Path.GetExtension(filename), Funscript.AudioSignatureExtension, StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetExtension(filename), Funscript.FunscriptExtension, StringComparison.OrdinalIgnoreCase))
            {
                WriteInfo($"Loading audio signature from '{filename}'...");
                return Convert(Funscript.FromFile(filename).AudioSignature);
            }
            else
            {
                WriteInfo($"Extraction audio signature from '{filename}'...");
                return AudioTracksAnalyzer.ExtractSignature(filename);
            }
        }

        public int Execute()
        {
            WriteInfo($"Loading source funscript '{r_options.SourceFunscript}'...");
            var inputFunscript = Funscript.FromFile(r_options.SourceFunscript);
            AudioSignature inputAudioSignature;
            if (r_options.SourceAudio != null)
            {
                inputAudioSignature = GetAudioSignature(r_options.SourceAudio);
            }
            else if (inputFunscript.AudioSignature != null)
            {
                WriteInfo($"Using audio signature from source funcript...");
                inputAudioSignature = Convert(inputFunscript.AudioSignature);
            }
            else
            {
                var videoFilename = Path.ChangeExtension(r_options.SourceFunscript, r_options.VideoExtension);
                inputAudioSignature = GetAudioSignature(videoFilename);
            }

            var outputAudioSignature = GetAudioSignature(r_options.NewAudio);

            WriteInfo($"Comparing audio signatures...");
            SamplesComparer comparer = new SamplesComparer(
                        inputAudioSignature,
                        outputAudioSignature,
                        new CompareOptions
                        {
                            MinimumMatchLength = TimeSpan.FromSeconds(r_options.MinimumMatchLength),
                            NbLocationsPerMinute = r_options.NbLocationsPerMinute
                        });
            var audioOffsets = comparer.FindAudioOffsets(WriteVerbose);

            WriteInfo();
            WriteInfo("Generating actions synchronized to the second file...");
            inputFunscript.Actions = TransformsActions(audioOffsets, inputFunscript.Actions);

            var newFilename = r_options.OutputFunscript ?? Path.ChangeExtension(r_options.NewAudio, Funscript.FunscriptExtension);
            WriteInfo($"Saving synchronized version '{newFilename}'.", ConsoleColor.Green);
            inputFunscript.AudioSignature = Convert(outputAudioSignature);
            inputFunscript.AddNotes(NotesSynchronizedByFunscriptToolbox);
            this.FunscriptVault.SaveFunscript(inputFunscript, newFilename);
            return 0;
        }
    }
}
