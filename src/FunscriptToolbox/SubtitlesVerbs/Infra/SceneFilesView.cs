using FunscriptToolbox.SubtitlesVerbs.Progress;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    /// <summary>
    /// The 'Files' tab of a scene in the web UI (see ISceneFilesView): the files of the scene next to its video(s) (the ones whose name is
    /// the name of the container or of one of its parts, then '.', and the configuration files of that folder) and all the files of its
    /// backup folder (known once the .wipsubtitles / the configuration are loaded: Global.BackupParentFolder can move it).
    /// Only these files can be read (by the kind of their folder and their exact name: never a path coming from the page).
    /// </summary>
    public class SceneFilesView : ISceneFilesView
    {
        public const string SceneFolderKind = "scene";
        public const string BackupFolderKind = "backup";

        /// <summary>A text longer than this is cut (a JSON cut can't be shown as a tree anymore: high enough for a .wipsubtitles).</summary>
        private const int MaxTextChars = 30_000_000;

        /// <summary>A file bigger than this is not sent (a video...).</summary>
        private const long MaxFileBytes = 60_000_000;

        /// <summary>The binary data in a text (ex. the images / the audio of an AI request, in base64): replaced by a note.</summary>
        private static readonly Regex rs_base64Regex = new Regex(@"[A-Za-z0-9+/=]{400,}", RegexOptions.Compiled);

        /// <summary>The time of the run at the start of the name of a file of the backup folder (see SubtitleGeneratorContext.GetPotentialVerboseFilePath).</summary>
        private static readonly Regex rs_runTimeRegex = new Regex(@"^(?<time>\d{14})_(?<rest>.*)$", RegexOptions.Compiled);

        /// <summary>An old version of a file, moved in the backup folder (see SubtitleGeneratorContext.SoftDelete): its name, '-', 12 hex, its extension.</summary>
        private static readonly Regex rs_oldVersionRegex = new Regex(@"^(?<name>.+)-[0-9a-f]{12}(?<ext>\.[^.]+)$", RegexOptions.Compiled);

        private static readonly Regex rs_aiFileRegex = new Regex(
            @"^(?<group>.+?)-(?<kind>Req|Resp|Resp-Content|Batch-Init-Resp|Batch-Polling-\d+-Resp)\.(json|txt)$", RegexOptions.Compiled);

        private static readonly HashSet<string> rs_textExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".json", ".txt", ".srt", ".ass", ".vtt", ".config", ".bak", ".wipsubtitles", ".vseq", ".log", ".md", ".csv", ".xml", ".asjson"
        };
        private static readonly Dictionary<string, string> rs_imageTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png", [".webp"] = "image/webp"
        };
        private static readonly Dictionary<string, string> rs_audioTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".wav"] = "audio/wav", [".mp3"] = "audio/mpeg", [".ogg"] = "audio/ogg", [".flac"] = "audio/flac"
        };
        private static readonly HashSet<string> rs_videoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mkv", ".avi", ".mov", ".webm", ".m4v", ".wmv"
        };

        private readonly SubtitleGeneratorContext r_context;
        private readonly string r_sceneFolder;
        private readonly string[] r_baseNames;

        /// <param name="containerFullPath">The video, or the .vseq of the parts.</param>
        /// <param name="videoFullPaths">The video(s) of the scene (the parts of a .vseq).</param>
        public SceneFilesView(SubtitleGeneratorContext context, string containerFullPath, IEnumerable<string> videoFullPaths)
        {
            r_context = context;
            r_sceneFolder = Path.GetDirectoryName(Path.GetFullPath(containerFullPath));
            r_baseNames = new[] { containerFullPath }
                .Concat(videoFullPaths ?? Array.Empty<string>())
                .Select(Path.GetFileNameWithoutExtension)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private class Entry
        {
            public string Folder;
            public string FullPath;
            public string Name;
            public long Size;
            public DateTime Date;
            public string Type;
            public string Group;
            public string Viewer;
        }

        /// <summary>The backup folder of the scene (null: not known yet, the .wipsubtitles is not loaded).</summary>
        private string BackupFolder => r_context.WIP?.BackupFolder;

        public object List()
        {
            var backup = BackupFolder;
            var entries = Enumerate();
            return new
            {
                folders = new object[]
                {
                    new { kind = SceneFolderKind, path = r_sceneFolder, exists = Directory.Exists(r_sceneFolder) },
                    new { kind = BackupFolderKind, path = backup, exists = backup != null && Directory.Exists(backup) }
                },
                verbose = r_context.IsVerbose,
                files = entries.Select(e => new
                {
                    folder = e.Folder,
                    name = e.Name,
                    type = e.Type,
                    group = e.Group,
                    date = new DateTimeOffset(e.Date).ToUnixTimeMilliseconds(),
                    size = e.Size,
                    viewer = e.Viewer
                }).ToArray()
            };
        }

        public SceneFileContent Read(string folder, string name)
        {
            // (only a name of the list: no path, nothing outside the 2 folders)
            if (string.IsNullOrEmpty(name) || name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || name.Contains(".."))
                return null;
            var entry = Enumerate().FirstOrDefault(e => e.Folder == folder && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            if (entry == null || entry.Viewer == null)
                return null;
            if (entry.Size > MaxFileBytes)
                throw new Exception($"The file is too big to be shown here ({entry.Size / 1_000_000} MB).");

            var extension = Path.GetExtension(entry.Name);
            switch (entry.Viewer)
            {
                case "image":
                    return new SceneFileContent { ContentType = rs_imageTypes[extension], Bytes = File.ReadAllBytes(entry.FullPath) };
                case "audio":
                    return new SceneFileContent { ContentType = rs_audioTypes[extension], Bytes = File.ReadAllBytes(entry.FullPath) };
                default:
                    var text = ReadShared(entry.FullPath);
                    text = rs_base64Regex.Replace(text, m => $"<{m.Length} chars of binary data (base64) not shown>");
                    if (text.Length > MaxTextChars)
                        text = text.Substring(0, MaxTextChars) + $"\n[... cut: {text.Length - MaxTextChars} more chars ...]";
                    return new SceneFileContent { ContentType = "text/plain; charset=utf-8", Bytes = Encoding.UTF8.GetBytes(text) };
            }
        }

        /// <summary>(the file can be written by the scene at the same time, ex. the .wipsubtitles)</summary>
        private static string ReadShared(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }

        private List<Entry> Enumerate()
        {
            var entries = new List<Entry>();
            if (Directory.Exists(r_sceneFolder))
            {
                foreach (var path in Directory.EnumerateFiles(r_sceneFolder))
                {
                    var name = Path.GetFileName(path);
                    var ofScene = r_baseNames.Any(b => name.StartsWith(b + ".", StringComparison.OrdinalIgnoreCase))
                        || name.StartsWith("--FSTB-SubtitleGenerator", StringComparison.OrdinalIgnoreCase);
                    if (ofScene)
                        entries.Add(CreateEntry(SceneFolderKind, path));
                }
            }
            var backup = BackupFolder;
            if (backup != null && Directory.Exists(backup))
            {
                foreach (var path in Directory.EnumerateFiles(backup))
                    entries.Add(CreateEntry(BackupFolderKind, path));
            }
            return entries;
        }

        private static Entry CreateEntry(string folder, string path)
        {
            var info = new FileInfo(path);
            var name = info.Name;
            var entry = new Entry { Folder = folder, FullPath = path, Name = name, Size = info.Length, Date = info.LastWriteTime };

            // (the time of the run, at the start of the name: the date shown, and not part of the group)
            var rest = name;
            var run = rs_runTimeRegex.Match(name);
            if (run.Success)
            {
                rest = run.Groups["rest"].Value;
                if (DateTime.TryParseExact(run.Groups["time"].Value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var runTime))
                    entry.Date = runTime;
            }

            var extension = Path.GetExtension(name);
            entry.Viewer = rs_textExtensions.Contains(extension) ? "text"
                : rs_imageTypes.ContainsKey(extension) ? "image"
                : rs_audioTypes.ContainsKey(extension) ? "audio"
                : null;

            var ai = rs_aiFileRegex.Match(rest);
            var oldVersion = folder == BackupFolderKind ? rs_oldVersionRegex.Match(rest) : Match.Empty;
            if (ai.Success)
            {
                var kind = ai.Groups["kind"].Value;
                entry.Type = kind == "Req" ? "AI request" : kind.StartsWith("Batch") ? "AI batch" : "AI response";
                entry.Group = ai.Groups["group"].Value;
            }
            else if (oldVersion.Success)
            {
                entry.Type = "Old version";
                entry.Group = oldVersion.Groups["name"].Value + oldVersion.Groups["ext"].Value;
            }
            else if (rs_audioTypes.ContainsKey(extension))
                entry.Type = folder == BackupFolderKind ? "Audio clip" : "Audio";
            else if (rs_imageTypes.ContainsKey(extension))
                entry.Type = folder == BackupFolderKind ? "Screenshot" : "Image";
            else if (rs_videoExtensions.Contains(extension))
                entry.Type = "Video";
            else if (name.StartsWith("--FSTB-SubtitleGenerator", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".used.config", StringComparison.OrdinalIgnoreCase))
                entry.Type = "Configuration";
            else
            {
                entry.Type = extension.ToLowerInvariant() switch
                {
                    ".srt" or ".ass" or ".vtt" => "Subtitles",
                    ".wipsubtitles" => "Work in progress",
                    ".vseq" => "Video sequence",
                    ".txt" => "Text",
                    ".json" => "JSON",
                    "" => "(no extension)",
                    _ => extension.ToLowerInvariant()
                };
            }
            return entry;
        }
    }
}
