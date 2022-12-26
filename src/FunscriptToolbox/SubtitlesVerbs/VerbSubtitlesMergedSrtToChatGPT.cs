//using CommandLine;
//using CommandLine.Text;
//using log4net;
//using System.Collections.Generic;
//using System.IO;
//using System.Linq;
//using System;
//using FunscriptToolbox.Core;

//namespace FunscriptToolbox.SubtitlesVerb
//{
//    class VerbSubtitlesMergedSrtToChatGPT : Verb
//    {
//        private static readonly ILog rs_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

//        [Verb("subtitles.mergedsrt2chatgpt", aliases: new[] { "sub.ms2gpt" }, HelpText = "Merge srts files (from whisper-translated .srt of a extracted wav from SubtitleEdit)")]
//        public class Options : OptionsBase
//        {
//            [Value(0, MetaName = "files", Required = true, HelpText = ".srt files or a folder")]
//            public IEnumerable<string> Input { get; set; }

//            [Usage(ApplicationAlias = Verb.ApplicationName)]
//            public static IEnumerable<Example> Examples
//            {
//                get
//                {
//                    yield break;
//                }
//            }
//        }

//        private readonly Options r_options;

//        public VerbSubtitlesMergedSrtToChatGPT(Options options)
//            : base(rs_log, options)
//        {
//            r_options = options;
//        }

//        public int Execute()
//        {
//            foreach (var filepath in r_options
//                .Input
//                .SelectMany(fileOrFolder => ExpandFolderToFiles(fileOrFolder, $"*{SubtitleFile.SrtExtension}"))
//                .SelectMany(file => HandleStarAndRecusivity(file)))
//            {
//                var filename = Path.GetFileName(filepath);
//                if (string.Equals(Path.GetExtension(filepath), SubtitleFile.SrtExtension, StringComparison.OrdinalIgnoreCase))
//                {
//                    var srt = SubtitleFile.FromSrtFile(filepath);
//                    var index = 1;
//                    File.WriteAllLines(
//                        Path.Combine(
//                            Path.GetDirectoryName(filepath),
//                            Path.GetFileNameWithoutExtension(filepath) + "-onlytext.txt"),
//                        srt.Subtitles.SelectMany(
//                            subtitle => new[] { $"[{index++:D4}]" }.Concat(subtitle.Lines)));
//                }
//                else
//                {
//                    WriteInfo($"{filename}: Ignoring. Only {SubtitleFile.SrtExtension} files are supported.", ConsoleColor.Yellow);
//                }
//            }
//            return base.NbErrors;
//        }
//    }
