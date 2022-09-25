using AudioSynchronization;
using CommandLine;
using System;
using System.Collections.Generic;
using System.IO;

namespace FunscriptToolbox.AudioSyncVerbs
{
    internal class VerbAudioSyncCreateFunscript : Verb
    {
        [Verb("audiosync.createFunscript", aliases: new[] { "as.cfs" }, HelpText = "Take an audio signature and funscript and try to generate a funscript synchronized to a different videos.")]
        public class Options : OptionsBase
        {
            [Value(0, MetaName = "inputFunscript", Required = true, HelpText = "original .funscript file")]
            public string InputFunscript { get; set; }

            [Value(1, MetaName = "inputVideoOrAsig", Required = true, HelpText = "original .mp4, .asig or .funscript file")]
            public string InputAudio { get; set; }

            [Value(2, MetaName = "outputVideoOrAsig", Required = true, HelpText = "destination .mp4 or .asig")]
            public string OutputAudio { get; set; }

            [Option('m', "minimumMatchLength", Required = false, Hidden = true, HelpText = "Minimum match length in second (default: 10)")]
            public int MinimumMatchLength { get; set; } = 10;

            [Option('p', "nbPeaksPerMinute", Required = false, Hidden = true, HelpText = "Nb peaks to test per minutes of video (default: 10)")]
            public int NbPeaksPerMinute { get; set; } = 10;
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
            return string.Equals(Path.GetExtension(filename), ".asig", StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetExtension(filename), ".funscript", StringComparison.OrdinalIgnoreCase)
                ? Funscript.FromFile(filename).AudioSignature
                : analyzer.ExtractSignature(r_options.InputAudio);
        }


        public int Execute()
        {
            var inputFunscript = Funscript.FromFile(r_options.InputFunscript);
            var inputAudioSignature = GetAudioSignature(r_options.InputAudio);
            var outputAudioSignature = GetAudioSignature(r_options.OutputAudio);

            SamplesComparer comparer = new SamplesComparer(
                        inputAudioSignature,
                        outputAudioSignature,
                        new CompareOptions
                        {
                            MinimumMatchLength = TimeSpan.FromSeconds(r_options.MinimumMatchLength),
                            NbPeaksPerMinute = r_options.NbPeaksPerMinute
                        });
            var audioOffsets = comparer.Compare();
            WriteInfo();
            WriteInfo();
            WriteInfo("Offsets:");
            foreach (var offset in audioOffsets)
            {
                WriteInfo($"From {offset.Start} to {offset.End}, substract {offset.Offset}");
            }
            WriteInfo();

            var newActions = new List<FunscriptActions>();
            var originalActions = inputFunscript.Actions;
            var nbUnmatchedActions = new List<FunscriptActions>();
            foreach (var action in originalActions)
            {
                var newAt = audioOffsets.TransformPosition(TimeSpan.FromMilliseconds(action.At));
                if (newAt != null)
                {
                    newActions.Add(new FunscriptActions { At = (int)newAt.Value.TotalMilliseconds, Pos = action.Pos });
                }
                else
                {
                    nbUnmatchedActions.Add(action);
                    audioOffsets.TransformPosition(TimeSpan.FromMilliseconds(action.At));
                }
            }
            WriteInfo($"{newActions.Count} actions moved, {nbUnmatchedActions.Count} actions ignored");

            inputFunscript.Actions = newActions.ToArray();
            inputFunscript.AudioSignature = outputAudioSignature;
            inputFunscript.Save(Path.ChangeExtension(r_options.OutputAudio, ".ft_gen.funscript"));
            return 0;
        }
    }
}
