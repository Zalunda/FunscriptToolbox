using AudioSynchronization;
using CommandLine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.AudioSyncVerbs
{
    internal class VerbAudioSyncCreateAudioSignature : Verb
    {
        [Verb("audiosync.createAudioSignature", aliases: new[] { "as.cas" }, HelpText = "Create audio signature for videos.")]
        public class Options : OptionsBase
        {
            [Value(0, MetaName = "files", Required = true, HelpText = "files (.mp4 => will create a .asig file, .funscript => audio signature will be embeded in the original funscript file)")]
            public IEnumerable<string> Files { get; set; }

            [Option('r', "recursive", Required = false, HelpText = "If a file contains '*', search recursivly for matches")]
            public bool Recursive { get; set; } = false;

            [Option('e', "videoextension", Required = false, HelpText = "If a file is a funscript, use this extension to find the corresponding video (default: mp4)")]
            public string VideoExtension { get; set; } = "mp4";
        }

        private readonly Options r_options;

        public VerbAudioSyncCreateAudioSignature(Options options)
            : base(options)
        {
            r_options = options;
        }

        public int Execute()
        {
            var analyzer = new AudioTracksAnalyzer();

            foreach (var file in r_options
                .Files
                .SelectMany(file => HandleStarAndRecusivity(file, r_options.Recursive)))
            {
                WriteInfo($"Creating audio signature for '{file}'.");

                if (string.Equals(Path.GetExtension(file), ".funscript", StringComparison.OrdinalIgnoreCase))
                {
                    var funscript = Funscript.FromFile(file);
                    funscript.AudioSignature = analyzer.ExtractSignature(Path.ChangeExtension(file, r_options.VideoExtension));
                    funscript.Save(Path.ChangeExtension(file, ".ft.funscript")); // TODO Save backup of original, save as .funscript
                }
                else
                {
                    var funscript = new Funscript
                    {
                        AudioSignature = analyzer.ExtractSignature(file)
                    };
                    funscript.Save(Path.ChangeExtension(file, ".asig"));
                }
            }

            return 0;
        }
    }
}
