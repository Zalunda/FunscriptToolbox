using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs
{
    /// <summary>
    /// A file (or a shortcut, ex. '[ja]' in the name of a folder) that defines values of the configuration of a scene, with its level:
    /// "0" = the main configuration, "0-S" = its '.override.config' (Standard), "0-U" = its '.override.user.config' (User), "1-S" = the
    /// '.override.config' of the first sub-folder, ..., "1-R" = a full configuration that Replaces everything in a sub-folder.
    /// Added as an annotation on the JTokens of the merged configuration (see SubtitleGeneratorConfigLoader.LoadHierarchicallyWithSources).
    /// </summary>
    public sealed class ConfigSource
    {
        public ConfigSource(string level, int depth, string kind, string filePath, string folder)
        {
            Level = level;
            Depth = depth;
            Kind = kind;
            FilePath = filePath;
            Folder = folder;
        }

        /// <summary>"0", "0-S", "0-U", "1-R", "1-S", "1-U", "1 (folder name)", "file name"...</summary>
        public string Level { get; }

        /// <summary>0: the folder of the main configuration, 1: its first sub-folder...</summary>
        public int Depth { get; }

        public const string KindMain = "main";
        public const string KindReplace = "replace";
        public const string KindOverride = "override";
        public const string KindUserOverride = "user";
        public const string KindFolderName = "folder-name";
        public const string KindFileName = "file-name";

        /// <summary>KindMain, KindReplace, KindOverride, KindUserOverride, KindFolderName or KindFileName.</summary>
        public string Kind { get; }

        /// <summary>The full path of the file (or of the folder / video for the shortcuts in their name).</summary>
        public string FilePath { get; }

        /// <summary>The folder, relative to the folder of the main configuration ("" for the main folder).</summary>
        public string Folder { get; }

        /// <summary>The file exists (a '.override.user.config' that could be created is listed too, see ConfigSourceTracker.Sources).</summary>
        public bool Exists { get; set; } = true;
    }

    /// <summary>The values replaced by a value of the configuration (annotation on its JToken): the oldest first.</summary>
    public sealed class ConfigSourceHistory
    {
        public ConfigSourceHistory(List<ConfigOverriddenValue> overridden)
        {
            Overridden = overridden;
        }

        public List<ConfigOverriddenValue> Overridden { get; }
    }

    public sealed class ConfigOverriddenValue
    {
        public ConfigOverriddenValue(ConfigSource source, string value)
        {
            Source = source;
            Value = value;
        }

        public ConfigSource Source { get; }

        /// <summary>The value, as text (cut when it's long).</summary>
        public string Value { get; }

        private const int MaxLength = 500;

        public static string ToText(JToken token)
        {
            var text = token == null
                ? "null"
                : token.Type == JTokenType.String ? (string)token : token.ToString(Formatting.None);
            return text.Length > MaxLength ? text.Substring(0, MaxLength) + "…" : text;
        }
    }

    /// <summary>
    /// A copy of a shared object, made for a '$ref' with other properties (annotation on the copy, see ExpandReferencesWithOverrides):
    /// the '$ref' itself (its source: where it was written) and the id of the shared object.
    /// </summary>
    public sealed class ConfigExpandedReference
    {
        public ConfigExpandedReference(string id, ConfigSource referenceSource)
        {
            Id = id;
            ReferenceSource = referenceSource;
        }

        public string Id { get; }
        public ConfigSource ReferenceSource { get; }
    }

    /// <summary>The configuration of a scene, with where each of its values comes from (see LoadHierarchicallyWithSources).</summary>
    public sealed class SubtitleGeneratorConfigWithSources
    {
        /// <summary>The configuration (null: it can't be loaded, see Error).</summary>
        public SubtitleGeneratorConfig Config { get; set; }

        /// <summary>The merged configuration, each JToken with its ConfigSource (and ConfigSourceHistory) annotations. Null: see Error.</summary>
        public JObject Merged { get; set; }

        /// <summary>The files read (and the '.override.user.config' that could be created), from the main configuration to the folder of the scene.</summary>
        public List<ConfigSource> Sources { get; } = new List<ConfigSource>();

        /// <summary>The configuration can't be loaded (null: no error).</summary>
        public string Error { get; set; }
    }
}
