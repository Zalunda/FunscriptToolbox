using AudioSynchronization;
using CommandLine;
using log4net;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

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

            [Option('d', "dumpoffsets", Required = false, HelpText = "Dump the offset in a file [first output filename].offsets.json", Default = false)]
            public bool DumpOffsets { get; internal set; }
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
                .OrderBy(f => f)
                .Select((mainFilename) => LoadAudioSignatureWithExtras($"I-{index++:D2}", mainFilename)))
                .ToArray();
            if (inputFiles.Length == 0)
            {
                throw new Exception($"No input files found.");
            }

            index = 1;
            var outputFiles = r_options
                .Outputs
                .SelectMany(file => HandleStarAndRecusivity(file)
                .OrderBy(f => f)
                .Select((mainFilename) => LoadAudioSignature($"O-{index++:D2}", mainFilename)))
                .ToArray();
            if (outputFiles.Length == 0)
            {
                throw new Exception($"No output files found.");
            }

            var duplicates = inputFiles
                .Where(input => outputFiles.Any(output =>
                    string.Equals(input.BaseFullPath, output.BaseFullPath, StringComparison.OrdinalIgnoreCase)))
                .Select(input => input.BaseFilename)
                .ToList();

            if (duplicates.Any())
            {
                var duplicateList = string.Join(Environment.NewLine, duplicates);
                throw new Exception($"The following files have been added to both input and output lists:{Environment.NewLine}{duplicateList}");
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
            var mergedOffsets = VirtualMergedAudioOffsetCollection.Create(audioOffsets, virtualInput, virtualOutput);
            WriteInfo($"Time to compare: {watchFindMatches.Elapsed}");           

            foreach (var item in virtualInput.MergedFunscriptFiles)
            {
                virtualOutput.AddFunscriptFile(item.Key, item.Value, mergedOffsets.TransformTimeRange);
            }

            foreach (var item in virtualInput.MergedSubtitleFiles)
            {
                virtualOutput.AddSubtitleFile(item.Key, item.Value, mergedOffsets.TransformTimeRange);
            }

            WriteInfo();
            WriteInfo($"Differences (Merged Inputs vs. Merged Outputs)");
            WriteInfo($"   {"Input",-5} {"Start",-14} {"End",-14} {"Offset",-14} {"Output",-5} {"Start",-14} {"End",-14} Actions Chapters Subtitles");
            WriteInfo($"   {new string('-', 114)}");
            foreach (var item in mergedOffsets)
            {
                var sb = new StringBuilder();
                sb.Append("   ");
                sb.Append((item.InputFile == null)
                    ? new string('.', 33) + "  "
                    : $"{item.InputFile.Id,-5} {FormatTimeSpan(item.InputStartTime),-14} {FormatTimeSpan(item.InputStartTime + item.Duration),-14}");
                sb.Append($" ");
                sb.Append($"{FormatTimeSpan(item.Offset),-14}");
                sb.Append($" ");
                sb.Append((item.OutputFile == null)
                    ? new string('.', 33) + "  "
                    : $"{item.OutputFile.Id,-5} {FormatTimeSpan(item.OutputStartTime),-14} {FormatTimeSpan(item.OutputStartTime + item.Duration),-14}");
                sb.Append($" ");
                sb.Append(item.Usage.All(u => u.Value == 0)
                    ? new string(' ', 26)
                    : item.OutputFile == null
                    ? $"{-item.Usage[ItemType.Actions],7} {-item.Usage[ItemType.Chapters],8} {-item.Usage[ItemType.Subtitles],9}"
                    : $"{item.Usage[ItemType.Actions],7} {item.Usage[ItemType.Chapters],8} {item.Usage[ItemType.Subtitles],9}");

                WriteInfo(sb.ToString());
            }

            if (r_options.DumpOffsets)
            {
                WriteInfo();
                WriteInfo($"Saving .offsets.json file.");
                File.WriteAllText(
                    outputFiles.First().BaseFullPath + ".offsets.json",
                    JsonConvert.SerializeObject(
                        mergedOffsets,
                        Formatting.Indented,
                        new JsonSerializerSettings
                        {
                            NullValueHandling = NullValueHandling.Ignore
                        }));
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
