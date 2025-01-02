using AudioSynchronization;
using CommandLine;
using log4net;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace FunscriptToolbox.AudioSyncVerbs
{
    internal class VerbAudioSyncCreateFunscript : VerbAudioSync
    {
        private static readonly ILog rs_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        [Verb("audiosync.createfunscript", aliases: new[] { "as.cfs" }, HelpText = "Take an audio signature and funscript and try to generate a funscript synchronized to a different videos.")]
        public class Options : OptionsBase
        {
            [Option('i', "input", Required = true, HelpText = "original .funscript file", Separator = ';')]
            public IEnumerable<string> Inputs { get; set; }

            [Option('o', "output", Required = true, HelpText = "new .mp4 or .asig file", Separator = ';')]
            public IEnumerable<string> Outputs { get; set; }

            [Option('m', "minimumMatchLength", Required = false, HelpText = "Minimum match length, in second (used by the 'matching algorythm')", Default = 60)]
            public int MinimumMatchLength { get => m_minimumMatchLength; set => m_minimumMatchLength = ValidateMinValue(value, 5); }
            private int m_minimumMatchLength;

            [Option('p', "nbLocationsPerMinute", Required = false, HelpText = "Number of locations to try to match per minute of video (used by the 'matching algorythm')", Default = 5)]
            public int NbLocationsPerMinute { get => m_nbLocationsPerMinute; set => m_nbLocationsPerMinute = ValidateMinValue(value, 2); }
            private int m_nbLocationsPerMinute;

            [Option('e', "videoextension", Required = false, HelpText = "If a file is a funscript, use this extension to find the corresponding video", Default = ".mp4")]
            public string VideoExtension { get; set; }

            [Option('d', "dontaddsignature", Required = false, HelpText = "Don't add the audio signature in generated funscript", Default = false)]
            public bool DontAddAudioSignature { get; internal set; }

            [Option('c', "alwayscreate", Required = false, HelpText = "Always create funscript even if empty", Default = false)]
            public bool AlwaysCreate { get; internal set; }
        }

        private readonly Options r_options;

        public VerbAudioSyncCreateFunscript(Options options)
            : base(rs_log, options, options.VideoExtension)
        {
            r_options = options;
        }

        public int Execute()
        {
            var index = 1;
            var inputFiles = r_options
                .Inputs
                .SelectMany(file => HandleStarAndRecusivity(file)
                .Select((mainFilename) => LoadAudioSignatureWithExtras($"Input-{index++:D2}", mainFilename)))
                .ToArray();
            if (inputFiles.Length == 0)
            {
                throw new Exception($"No input files found.");
            }

            index = 1;
            var outputFiles = r_options
                .Outputs
                .SelectMany(file => HandleStarAndRecusivity(file)
                .Select((mainFilename) => LoadAudioSignature($"Output-{index++:D2}", mainFilename)))
                .ToArray();
            if (outputFiles.Length == 0)
            {
                throw new Exception($"No output files found.");
            }

            if (inputFiles.Any(input => 
                outputFiles.Any(output =>
                string.Equals(input.BaseFullPath, output.BaseFullPath, StringComparison.OrdinalIgnoreCase))))
            {
                throw new Exception($"A file has been added to both input and output lists.");
            }

            var virtualInput = new VirtualMergedFile("MergedInput", inputFiles);
            var virtualOutput = new VirtualMergedFile("MergedOutput", outputFiles);

            WriteInfo();
            WriteInfo($"Comparing audio signatures (Merged Inputs vs. Merged Outputs)...");
            SamplesComparer comparer = new SamplesComparer(
                        virtualInput.MergedAudioSignature,
                        virtualOutput.MergedAudioSignature,
                        new CompareOptions
                        {
                            MinimumMatchLength = TimeSpan.FromSeconds(r_options.MinimumMatchLength),
                            NbLocationsPerMinute = r_options.NbLocationsPerMinute
                        });
            var watchFindMatches = Stopwatch.StartNew();
            var audioOffsets = comparer.FindAudioOffsets(WriteVerbose);
            WriteInfo($"Time to compare: {watchFindMatches.Elapsed}");

            foreach (var item in virtualInput.MergedFunscriptFiles)
            {
                virtualOutput.AddFunscriptFile(item.Key, item.Value, audioOffsets.TransformPosition);
            }

            foreach (var item in virtualInput.MergedSubtitleFiles)
            {
                virtualOutput.AddSubtitleFile(item.Key, item.Value, audioOffsets.TransformPosition);
            }

            WriteInfo();
            WriteInfo($"Differences (Merged Inputs vs. Merged Outputs)");
            foreach (var item in audioOffsets)
            {
                if (item.Offset == null)
                    WriteInfo($"   From {FormatTimeSpan(item.StartTime),-12} to {FormatTimeSpan(item.EndTime),-12}, {item.NbTimesUsed,5} actions, chapters or subtitles have been DROPPED");
                else if (item.Offset == TimeSpan.Zero)
                    WriteInfo($"   From {FormatTimeSpan(item.StartTime),-12} to {FormatTimeSpan(item.EndTime),-12}, {item.NbTimesUsed,5} actions, chapters or subtitles copied as is");
                else
                    WriteInfo($"   From {FormatTimeSpan(item.StartTime),-12} to {FormatTimeSpan(item.EndTime),-12}, {item.NbTimesUsed,5} actions, chapters or subtitles have been MOVED by {FormatTimeSpan(item.Offset.Value)}");
            }

            WriteInfo();
            foreach (var file in virtualOutput.Files)
            {
                foreach (var item in file.Funscripts)
                {
                    if (r_options.AlwaysCreate || item.Value.Actions.Length > 0 || item.Value.GetClonedChapters().Count() > 0)
                    {
                        var funscript = item.Value;
                        funscript.AddNotes(NotesSynchronizedByFunscriptToolbox);
                        if (!r_options.DontAddAudioSignature)
                            funscript.AudioSignature = Convert(file.AudioSignature);
                        funscript.Duration = (int)file.Duration.TotalSeconds;
                        WriteInfo($"Saving '{file.BaseFullPath + item.Key}'...");
                        funscript.Save(file.BaseFullPath + item.Key);

                    }
                }
                foreach (var item in file.Subtitles)
                {
                    if (r_options.AlwaysCreate || item.Value.Subtitles.Count > 0)
                    {
                        item.Value.SaveSrt(file.BaseFullPath + item.Key);
                        WriteInfo($"Saving '{file.BaseFullPath + item.Key}'...");
                    }
                }
            }
            return 0;
        }
    }
}
