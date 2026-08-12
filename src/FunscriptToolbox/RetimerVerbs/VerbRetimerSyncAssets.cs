using CommandLine;
using log4net;
using Newtonsoft.Json;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.RetimerVerbs
{
    class VerbRetimerSyncAssets : VerbRetimerBase
    {
        private static readonly ILog rs_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        [Verb("retimer.syncassets", aliases: new[] { "retimer.sync" }, HelpText = "Synchronize original subtitles and funscripts to the retimed video using the offsets map.")]
        public class Options : OptionsBase
        {
            [Option("original-video", Required = true, HelpText = "Path to the ORIGINAL video (used to find original .srt/.funscript sidecars).")]
            public string OriginalVideo { get; set; }

            [Option("retimed-video", Required = false, HelpText = "Path to the RETIMED video (used to dictate the output path of the newly synced sidecar files). If omitted, it assumes <original-video>.retimed.mp4.")]
            public string RetimedVideo { get; set; }

            [Option("offsets-map", Required = false, HelpText = "Path to the .offsets.json file. If omitted, it assumes <retimed-video>.offsets.json.")]
            public string OffsetsMap { get; set; }

            [Option("sync-video", Required = false, HelpText = "If true, compare the new Original Video audio with the one in the .offset.json file.")]
            public bool SyncVideo { get; set; }
        }

        private readonly Options r_options;

        public VerbRetimerSyncAssets(Options options) : base(rs_log, options)
        {
            r_options = options;
        }

        public int Execute()
        {
            r_options.RetimedVideo ??= Path.ChangeExtension(r_options.OriginalVideo, ".retimed.mp4");

            string offsetsFilePath = string.IsNullOrWhiteSpace(r_options.OffsetsMap)
                ? Path.ChangeExtension(r_options.RetimedVideo, ".offsets.json")
                : r_options.OffsetsMap;

            if (!File.Exists(offsetsFilePath))
            {
                WriteError($"Offsets map file not found: {offsetsFilePath}. Render video first to generate map.");
                return 1;
            }

            var offsetsFile = JsonConvert.DeserializeObject<RetimerSyncOffsetFile>(File.ReadAllText(offsetsFilePath));
            if (offsetsFile.Offsets == null || !offsetsFile.Offsets.Any())
            {
                WriteError("Offsets map is empty or invalid.");
                return 1;
            }

            WriteInfo($"Starting Asset Synchronization...");
            WriteInfo($"Original: {r_options.OriginalVideo}");
            WriteInfo($"Retimed Output Target: {r_options.RetimedVideo}");

            SyncSidecarAssets(r_options.OriginalVideo, r_options.RetimedVideo, offsetsFile.Offsets);

            WriteInfo("Asset synchronization complete.");
            return 0;
        }
    }
}