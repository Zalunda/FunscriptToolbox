﻿using AudioSynchronization;
using CommandLine;
using CommandLine.Text;
using System;
using System.Collections.Generic;
using System.IO;

namespace FunscriptToolbox.AudioSyncVerbs
{
    internal class VerbAudioSyncCreateFunscript : Verb
    {
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

            [Usage(ApplicationAlias = "FunscriptToolBox")]
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
            : base(options)
        {
            r_options = options;
        }

        private AudioSignature GetAudioSignature(string filename)
        {
            var analyzer = new AudioTracksAnalyzer();

            if (string.Equals(Path.GetExtension(filename), Funscript.AudioSignatureExtension, StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetExtension(filename), Funscript.FunscriptExtension, StringComparison.OrdinalIgnoreCase))
            {
                WriteInfo($"Loading audio signature from '{filename}'...");
                return Funscript.FromFile(filename).AudioSignature;
            }
            else
            {
                WriteInfo($"Extraction audio signature from '{filename}'...");
                return analyzer.ExtractSignature(filename);
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
                inputAudioSignature = inputFunscript.AudioSignature;
            }
            else
            {
                var videoFilename = Path.ChangeExtension(r_options.SourceFunscript, r_options.VideoExtension);
                inputAudioSignature = GetAudioSignature(videoFilename);
            }

            var outputAudioSignature = GetAudioSignature(r_options.NewAudio);

            SamplesComparer comparer = new SamplesComparer(
                        inputAudioSignature,
                        outputAudioSignature,
                        new CompareOptions
                        {
                            MinimumMatchLength = TimeSpan.FromSeconds(r_options.MinimumMatchLength),
                            NbLocationsPerMinute = r_options.NbLocationsPerMinute
                        });
            WriteInfo($"Comparing audio signatures...");
            var audioOffsets = comparer.FindAudioOffsets();

            WriteInfo();
            WriteInfo();
            WriteInfo("Generating actions synchronized to the second file...");
            var newActions = new List<FunscriptAction>();
            var originalActions = inputFunscript.Actions;
            foreach (var action in originalActions)
            {
                var newAt = audioOffsets.TransformPosition(TimeSpan.FromMilliseconds(action.At));
                if (newAt != null)
                {
                    newActions.Add(new FunscriptAction { At = (int)newAt.Value.TotalMilliseconds, Pos = action.Pos });
                }
            }

            foreach (var item in audioOffsets)
            {
                if (item.Offset == null)
                    WriteInfo($"   From {FormatTimeSpan(item.Start),-12} to {FormatTimeSpan(item.End),-12}, {item.NbTimesUsed,5} actions have been DROPPED");
                else if (item.Offset == TimeSpan.Zero)
                    WriteInfo($"   From {FormatTimeSpan(item.Start),-12} to {FormatTimeSpan(item.End),-12}, {item.NbTimesUsed,5} actions copied as is");
                else
                    WriteInfo($"   From {FormatTimeSpan(item.Start),-12} to {FormatTimeSpan(item.End),-12}, {item.NbTimesUsed,5} actions have been MOVED by {FormatTimeSpan(item.Offset.Value)}");
            }
            WriteInfo();

            inputFunscript.Actions = newActions.ToArray();
            inputFunscript.AudioSignature = outputAudioSignature;
            this.FunscriptVault.SaveFunscript(
                inputFunscript, 
                r_options.OutputFunscript ?? Path.ChangeExtension(r_options.NewAudio, Funscript.FunscriptExtension));
            return 0;
        }
    }
}