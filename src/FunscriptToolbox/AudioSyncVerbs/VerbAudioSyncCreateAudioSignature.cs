using AudioSynchronization;
using CommandLine;
using CommandLine.Text;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.AudioSyncVerbs
{
    internal class VerbAudioSyncCreateAudioSignature : Verb
    {
        [Verb("audiosync.createaudiosignature", aliases: new[] { "as.cas" }, HelpText = "Create audio signature for videos.")]
        public class Options : OptionsBase
        {
            [Value(0, MetaName = "files", Required = true, HelpText = "files (see examples)")]
            public IEnumerable<string> Files { get; set; }

            [Option('r', "recursive", Required = false, HelpText = "If a file contains '*', allow to search recursivly for matches", Default = false)]
            public bool Recursive { get; set; }

            [Option('f', "force", Required = false, HelpText = "If a funscript already contains an audio signature, allow to replace it", Default = false)]
            public bool Force { get; set; }

            [Option('e', "videoextension", Required = false, HelpText = "If a file is a funscript, use this extension to find the corresponding video", Default = ".mp4")]
            public string VideoExtension { get; set; }

            [Usage(ApplicationAlias = "FunscriptToolBox")]
            public static IEnumerable<Example> Examples
            {
                get
                {
                    yield return new Example(
                        "Add an audio signature to an existing funscript file",
                        DefaultUnparserSettings,
                        new Options { Files = new[] { "video.funscript" } });
                    yield return new Example(
                        $"Create an audio signature file (extension = '{Funscript.AudioSignatureExtension}') for a video",
                        DefaultUnparserSettings,
                        new Options { Files = new[] { "video.mp4" } });
                    yield return new Example(
                        "Add or create an audio signature for all funscripts or videos that doesn't already have one, recursively",
                        DefaultUnparserSettings,
                        new Options { Recursive = true, Files = new[] { "*.funscript", "*.mp4" } });
                }
            }
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
                try
                {
                    if (string.Equals(Path.GetExtension(file), Funscript.FunscriptExtension, StringComparison.OrdinalIgnoreCase))
                    {
                        var funscript = Funscript.FromFile(file);
                        if ((funscript.AudioSignature == null) || r_options.Force)
                        {
                            var videoFile = Path.ChangeExtension(file, r_options.VideoExtension);
                            if (File.Exists(videoFile))
                            {
                                WriteInfo($"{file}: Extraction audio signature from '{videoFile}'...");
                                funscript.AudioSignature = analyzer.ExtractSignature(videoFile);
                                WriteInfo($"{file}: Adding audio signature to file.");
                                this.FunscriptVault.SaveFunscript(
                                    funscript, 
                                    Path.ChangeExtension(file, Funscript.FunscriptExtension));
                            }
                            else
                            {
                                WriteInfo($"{file}: Skipping because there is no {r_options.VideoExtension} file with the same name.");
                            }
                        }
                        else
                        {
                            WriteInfo($"{file}: Skipping because it already contains au audio signature (use --force to override).");
                        }
                    }
                    else
                    {
                        var asigFilename = Path.ChangeExtension(file, Funscript.AudioSignatureExtension);
                        var funscriptFilename = Path.ChangeExtension(file, Funscript.FunscriptExtension);
                        var funscript = File.Exists(funscriptFilename) ? Funscript.FromFile(funscriptFilename) : null;
                        if (File.Exists(asigFilename) && !r_options.Force)
                        {
                            WriteInfo($"{file}: Skipping because '{asigFilename}' already exists (use --force to override).");
                        }
                        else if (funscript?.AudioSignature != null && !r_options.Force)
                        {
                            WriteInfo($"{file}: Skipping because '{funscriptFilename}' already contains an audio signature (use --force to override).");
                        }
                        else
                        {
                            WriteInfo($"{file}: Extracting audio signature from file...");
                            var asig = new Funscript
                            {
                                AudioSignature = analyzer.ExtractSignature(file)
                            };
                            WriteInfo($"{file}: Creating audio signature file '{asigFilename}'.");
                            this.FunscriptVault.SaveFunscript(
                                asig, 
                                asigFilename);
                        }
                    }
                }
                catch (Exception ex)
                {
                    WriteError($"{file}: An exception occured => {ex}");
                }
            }

            return NbErrors;
        }
    }
}
