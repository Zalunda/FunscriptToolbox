using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.AudioExtractions;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using FunscriptToolbox.SubtitlesVerbs.Outputs;
using FunscriptToolbox.SubtitlesVerbs.Transcriptions;
using FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner;
using FunscriptToolbox.SubtitlesVerbs.Translations;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerbs
{
    public class SubtitleGeneratorConfigLoader
    {
        /// <summary>
        /// The serializer of the configurations, one by thread: a JsonSerializer that preserves the references is NOT thread safe (its
        /// reference resolver numbers the objects with a counter: two threads at the same time => the same number twice, "Error writing /
        /// reading object reference"). The configurations are read by the threads of the scenes, of the main loop and of the web server.
        /// </summary>
        public static JsonSerializer rs_serializer => rs_serializerByThread.Value;

        private static readonly SimpleTypeNameSerializationBinder rs_binder = new SimpleTypeNameSerializationBinder(
            new[] {
                typeof(AIEngine),
                typeof(AIOptions),
                typeof(AIPrompt),
                typeof(BinaryDataExtractor),
                typeof(AudioExtractor),
                typeof(SubtitleOutput),
                typeof(TranscriberToolAudio),
                typeof(TimingRefineEngine),
                typeof(Transcriber),
                typeof(Translator)
            });

        private static readonly System.Threading.ThreadLocal<JsonSerializer> rs_serializerByThread = new System.Threading.ThreadLocal<JsonSerializer>(() =>
        {
            var serializer = JsonSerializer.Create(new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                SerializationBinder = rs_binder,
                TypeNameHandling = TypeNameHandling.Auto,
                PreserveReferencesHandling = PreserveReferencesHandling.Objects
            });
            serializer.Converters.Add(new StringEnumConverter());
            serializer.ReferenceResolver = new ValidatingReferenceResolver(serializer.ReferenceResolver);
            return serializer;
        });
        // This pattern looks for two letters inside square brackets, e.g., "[ja]".
        private static readonly Regex s_languageInFolderNameRegex = new Regex(@"\[(?<name>[a-zA-Z]{2})\]", RegexOptions.Compiled);

        /// <summary>
        /// Loads a base configuration and merges all override files found in the path
        /// leading from the base config's directory down to the target file's directory.
        /// </summary>
        /// <param name="baseConfigPath">The full path to the main configuration file.</param>
        /// <param name="targetFileFullPath">The full path of the video/wipsub file being processed.</param>
        /// <returns>A fully resolved and merged SubtitleGeneratorConfig object.</returns>
        // Helper function to read and parse a config file.
        private static JObject ReadHybridJsonFile(string path)
        {
            var content = File.ReadAllText(path);
            var adjustedContent = PreprocessHybridJsonFile(content);
            // We'll define a local version of HandleJsonException for simplicity.
            try
            {
                // Use JsonLoadSettings to enable line number tracking.
                var settings = new JsonLoadSettings
                {
                    LineInfoHandling = LineInfoHandling.Load
                };

                // Use a JsonTextReader to apply the settings.
                using (var reader = new JsonTextReader(new StringReader(adjustedContent)))
                {
                    return JObject.Load(reader, settings);
                }
            }
            catch (JsonException ex)
            {
                // (the line numbers are the ones of the file, see PreprocessHybridJsonFile)
                throw new Exception($"Error parsing configuration file '{path}': {ex.Message}", ex);
            }
        }

        /// <summary>
        /// The override files of a folder, in the order they are merged: '.override.config' (from the installation, replaced
        /// by a new version), then '.override.user.config' (the user's own changes: never created / replaced by the installation).
        /// </summary>
        private static readonly string[] rs_overrideConfigSuffixes = { ".override.config", ".override.user.config" };

        private static IEnumerable<string> GetOverrideConfigFileNames(string baseConfigPath)
        {
            return rs_overrideConfigSuffixes.Select(suffix => Path.GetFileNameWithoutExtension(baseConfigPath) + suffix);
        }

        /// <summary>
        /// The settings shared by all the scenes processed at the same time ('GlobalShared' section): only read from the main
        /// configuration file and the override files in the same folder (one value for the whole run).
        /// </summary>
        public static SubtitleGeneratorGlobalSharedSettings LoadGlobalShared(string baseConfigPath)
        {
            var config = ReadHybridJsonFile(baseConfigPath);
            var baseConfigDir = Path.GetDirectoryName(Path.GetFullPath(baseConfigPath));
            foreach (var overrideConfigFileName in GetOverrideConfigFileNames(baseConfigPath))
            {
                var overrideConfigPath = Path.Combine(baseConfigDir, overrideConfigFileName);
                if (File.Exists(overrideConfigPath))
                {
                    MergeConfigFile(config, ReadHybridJsonFile(overrideConfigPath), overrideConfigPath);
                }
            }

            var section = config.Property(GlobalSharedSectionName, StringComparison.OrdinalIgnoreCase)?.Value;
            try
            {
                return section?.ToObject<SubtitleGeneratorGlobalSharedSettings>(rs_serializer);
            }
            catch (JsonException ex)
            {
                throw new Exception($"Error in the '{GlobalSharedSectionName}' section of '{baseConfigPath}' (or its override files): {ex.Message}", ex);
            }
        }

        private const string GlobalSharedSectionName = "GlobalShared";

        /// <summary>A 'GlobalShared' section in a configuration file of a sub-folder can't be applied (one value for the whole run): error.</summary>
        private static void EnsureNoGlobalShared(JObject config, string path)
        {
            if (config.Property(GlobalSharedSectionName, StringComparison.OrdinalIgnoreCase) != null)
            {
                throw new Exception(
                    $"The '{GlobalSharedSectionName}' section is only read from the main configuration file (and the override files in the same folder), "
                    + $"because it applies to all the scenes at the same time. Remove it from '{path}'.");
            }
        }

        public static SubtitleGeneratorConfig LoadHierarchically(string baseConfigPath, string targetFileFullPath)
        {
            return LoadHierarchically(baseConfigPath, targetFileFullPath, tracking: null);
        }

        /// <summary>
        /// Same as LoadHierarchically, but each value of the merged configuration knows where it comes from (the 'Config' tab of the
        /// inspector of the web UI): a ConfigSource annotation on its JToken (and a ConfigSourceHistory with the values it replaced).
        /// Never throws, and writes nothing on disk: an error is returned in 'Error' (with the merged configuration when it could be merged).
        /// </summary>
        public static SubtitleGeneratorConfigWithSources LoadHierarchicallyWithSources(string baseConfigPath, string targetFileFullPath)
        {
            var result = new SubtitleGeneratorConfigWithSources();
            try
            {
                result.Config = LoadHierarchically(baseConfigPath, targetFileFullPath, result);
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }
            return result;
        }

        /// <summary>A configuration file, read like the others (comments, long texts '=____ ... ____='): for the writer of the 'Config' tab of the web UI.</summary>
        internal static JObject ReadConfigFile(string path) => ReadHybridJsonFile(path);

        /// <summary>
        /// The text of a configuration file (a '.override.user.config' written by the web UI): indented, the texts on several lines in the
        /// long text format ('=____ ... ____='), after the 'header' (comment lines).
        /// </summary>
        internal static string ToHybridConfigText(JObject config, string header)
        {
            var copy = (JObject)config.DeepClone();
            foreach (var value in copy.Descendants().OfType<JValue>().Where(v => v.Type == JTokenType.String && ((string)v).Contains('\n')).ToList())
                value.Value = CreateLongString((string)value.Value);
            var text = ReplaceLongStringFromJsonToHybrid(copy.ToString(Formatting.Indented));
            return (header == null ? string.Empty : header + Environment.NewLine) + text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        }

        /// <summary>A short hash of the merged configuration (to know if the files changed since a scene loaded its configuration).</summary>
        private static string HashOfMergedConfig(string finalConfigText)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                return Convert.ToBase64String(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(finalConfigText)));
            }
        }

        /// <summary>Each JToken of a file read gets its source (only when the sources are tracked).</summary>
        private static void AddSource(SubtitleGeneratorConfigWithSources tracking, JObject fileConfig, ConfigSource source)
        {
            if (tracking == null)
                return;
            tracking.Sources.Add(source);
            if (fileConfig == null)
                return;
            foreach (var token in fileConfig.DescendantsAndSelf())
                token.AddAnnotation(source);
        }

        /// <summary>
        /// A value of the configuration replaced (by a file, or by a shortcut): the new one keeps the old one (and the ones it had
        /// replaced) in a ConfigSourceHistory. Only when the sources are tracked (the old one has a ConfigSource).
        /// </summary>
        private static void RecordOverridden(JToken oldToken, JToken newToken, JToken userToken)
        {
            var oldSource = oldToken?.Annotation<ConfigSource>();
            if (oldSource == null || newToken == null)
                return;
            var overridden = new List<ConfigOverriddenValue>(oldToken.Annotation<ConfigSourceHistory>()?.Overridden ?? new List<ConfigOverriddenValue>())
            {
                new ConfigOverriddenValue(oldSource, ConfigOverriddenValue.ToText(oldToken))
            };
            // (a value that already replaced others: ex. a '$ref' with overrides, written in several files, then merged in the copy of
            // the shared object: the values of the shared object, then the ones it replaced in the step)
            overridden.AddRange(userToken?.Annotation<ConfigSourceHistory>()?.Overridden ?? Enumerable.Empty<ConfigOverriddenValue>());
            if (ReferenceEquals(oldToken, newToken))
            {
                // (the same value written again: Newtonsoft.Json keeps the old token, it gets the source of the file that wrote it again)
                var userSource = userToken?.Annotation<ConfigSource>();
                if (userSource == null)
                    return;
                newToken.RemoveAnnotations<ConfigSource>();
                newToken.AddAnnotation(userSource);
            }
            newToken.RemoveAnnotations<ConfigSourceHistory>();
            newToken.AddAnnotation(new ConfigSourceHistory(overridden));
        }

        /// <summary>The language of a shortcut ('[ja]' in the name of a folder / of the video) replaces 'SourceLanguage'.</summary>
        private static void SetSourceLanguageFromShortcut(JObject finalConfig, string langCode, SubtitleGeneratorConfigWithSources tracking, ConfigSource source)
        {
            var oldToken = finalConfig.Property("SourceLanguage", StringComparison.OrdinalIgnoreCase)?.Value;
            var newValue = new JValue(langCode);
            finalConfig["SourceLanguage"] = newValue;
            if (tracking != null)
            {
                tracking.Sources.Add(source);
                newValue.AddAnnotation(source);
                RecordOverridden(oldToken, finalConfig["SourceLanguage"], newValue);
            }
        }

        private static SubtitleGeneratorConfig LoadHierarchically(string baseConfigPath, string targetFileFullPath, SubtitleGeneratorConfigWithSources tracking)
        {
            // 1. Load the base configuration file.
            var finalConfig = ReadHybridJsonFile(baseConfigPath);
            var lastFullPath = baseConfigPath;

            // 2. Determine paths and the name for override files.
            var baseConfigDir = Path.GetFullPath(Path.GetDirectoryName(baseConfigPath));
            var targetFileDir = Path.GetFullPath(Path.GetDirectoryName(targetFileFullPath));
            AddSource(tracking, finalConfig, new ConfigSource("0", 0, ConfigSource.KindMain, Path.GetFullPath(baseConfigPath), string.Empty));

            // The name of the file that acts as a new root/base for a subtree.
            var rootConfigFileName = Path.GetFileName(baseConfigPath);
            var overrideConfigFileNames = GetOverrideConfigFileNames(baseConfigPath).ToArray();

            // 3. Ensure the target file is in a subdirectory of the base config.
            // If not, no overrides can be applied: only the base config.
            if (targetFileDir.StartsWith(baseConfigDir + @"\", StringComparison.OrdinalIgnoreCase))
            {
                // 4. Create a list of all directories to scan, from the base down to the target.
                var directoriesToScan = new List<string>();
                var currentDir = new DirectoryInfo(targetFileDir);

                // Traverse upwards from the target and add to list
                while (currentDir != null && currentDir.FullName.Length >= baseConfigDir.Length)
                {
                    directoriesToScan.Add(currentDir.FullName);
                    if (string.Equals(currentDir.FullName, baseConfigDir, StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                    currentDir = currentDir.Parent;
                }
                // Reverse the list to process from top-down (base -> target)
                directoriesToScan.Reverse();

                // 5. Traverse the directories, applying folder-name shortcuts and merging override files.
                for (var depth = 0; depth < directoriesToScan.Count; depth++)
                {
                    var dirPath = directoriesToScan[depth];
                    var dirInfo = new DirectoryInfo(dirPath);
                    var relativeFolder = Path.GetRelativePath(baseConfigDir, dirPath);
                    if (relativeFolder == ".") relativeFolder = string.Empty;

                    // Check the directory name for a language code like "[ja]".
                    var languageMatch = s_languageInFolderNameRegex.Match(dirInfo.Name);
                    if (languageMatch.Success)
                    {
                        string langCode = languageMatch.Groups["name"].Value;
                        // Validate that it's a real language.
                        if (Language.FromString(langCode) != null)
                        {
                            // It's valid, so override the "SourceLanguage" property in our JObject.
                            SetSourceLanguageFromShortcut(finalConfig, langCode, tracking,
                                new ConfigSource($"{depth} (folder name)", depth, ConfigSource.KindFolderName, dirPath, relativeFolder));
                        }
                    }

                    // (the 'GlobalShared' section is only allowed in the folder of the main configuration)
                    var isMainFolder = string.Equals(Path.GetFullPath(dirPath).TrimEnd('\\'), baseConfigDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

                    var rootConfigPath = Path.Combine(dirPath, rootConfigFileName);
                    if (File.Exists(rootConfigPath))
                    {
                        // Replace the config for the one we just found.
                        finalConfig = ReadHybridJsonFile(rootConfigPath);
                        lastFullPath = rootConfigPath;
                        if (!isMainFolder) EnsureNoGlobalShared(finalConfig, rootConfigPath);
                        // (the main configuration, read again: already in the sources)
                        if (tracking != null)
                        {
                            if (isMainFolder)
                                foreach (var token in finalConfig.DescendantsAndSelf())
                                    token.AddAnnotation(tracking.Sources[0]);
                            else
                                AddSource(tracking, finalConfig, new ConfigSource($"{depth}-R", depth, ConfigSource.KindReplace, rootConfigPath, relativeFolder));
                        }
                    }

                    // Now, check for the override files in the same directory ('.override.config', then '.override.user.config').
                    // This allows the file to override the folder-name shortcut if needed.
                    for (var index = 0; index < overrideConfigFileNames.Length; index++)
                    {
                        var overrideConfigPath = Path.Combine(dirPath, overrideConfigFileNames[index]);
                        var isUserOverride = index == overrideConfigFileNames.Length - 1;
                        var source = tracking == null
                            ? null
                            : new ConfigSource(
                                $"{depth}-{(isUserOverride ? "U" : "S")}",
                                depth,
                                isUserOverride ? ConfigSource.KindUserOverride : ConfigSource.KindOverride,
                                overrideConfigPath,
                                relativeFolder);
                        if (File.Exists(overrideConfigPath))
                        {
                            var overrideConfig = ReadHybridJsonFile(overrideConfigPath);
                            if (!isMainFolder) EnsureNoGlobalShared(overrideConfig, overrideConfigPath);
                            lastFullPath = overrideConfigPath;
                            AddSource(tracking, overrideConfig, source);
                            MergeConfigFile(finalConfig, overrideConfig, overrideConfigPath);
                        }
                        else if (tracking != null && isUserOverride)
                        {
                            // (a '.override.user.config' that could be created there)
                            source.Exists = false;
                            tracking.Sources.Add(source);
                        }
                    }
                }

                // 6. Final Override from Filename (Highest Precedence) ---
                string targetFileName = Path.GetFileName(targetFileFullPath);
                Match fileNameMatch = s_languageInFolderNameRegex.Match(targetFileName);
                if (fileNameMatch.Success)
                {
                    string langCode = fileNameMatch.Groups["name"].Value;
                    if (Language.FromString(langCode) != null)
                    {
                        // This will overwrite any language setting from the base config,
                        // folder names, or override files.
                        SetSourceLanguageFromShortcut(finalConfig, langCode, tracking,
                            new ConfigSource("file name", directoriesToScan.Count - 1, ConfigSource.KindFileName, targetFileFullPath, Path.GetRelativePath(baseConfigDir, targetFileDir)));
                    }
                }
            }

            if (tracking != null)
                tracking.Merged = finalConfig;

            // 7. The '$ref' with other properties are replaced by a copy of their object, with these properties merged in it.
            try
            {
                ExpandReferencesWithOverrides(finalConfig);
            }
            catch (Exception ex)
            {
                throw new Exception($"Error in the merged configuration for '{lastFullPath}': {ex.Message}", ex);
            }

            // 8. Deserialize the final JObject into a config object.
            var finalConfigText = finalConfig.ToString();
            try
            {
                var config = rs_serializer.Deserialize<SubtitleGeneratorConfig>(
                    new JsonTextReader(
                        new StringReader(finalConfigText)));
                config.MergedConfigHash = HashOfMergedConfig(finalConfigText);
                return config;
            }
            catch (JsonException ex) when (tracking == null)
            {
                var adjustedFileName = lastFullPath + ".as.json";
                File.WriteAllText(adjustedFileName, "ERROR:\n" + ex.Message + "\n\n--- JSON CONTENT ---\n" + finalConfigText);
                throw new Exception($"Error during final deserialization of merged config for '{lastFullPath}'. Details written to '{adjustedFileName}'.\n\nDetails: {ex.Message}", ex);
            }
            catch (JsonException ex)
            {
                // (the sources are tracked: nothing written on disk)
                throw new Exception($"Error during final deserialization of merged config for '{lastFullPath}': {ex.Message}", ex);
            }
        }

        // Internal class to define the rules for merging arrays.
        private class ArrayMergeRule
        {
            public string[] Path { get; }
            public string[] Keys { get; }
            public string FallbackIdentifierKey { get; }
            public Action<JArray, List<JObject>, ArrayMergeRule> NewItemHandler { get; }
            public string[] RequiredPropertiesForNewItems { get; }

            public ArrayMergeRule(
                string[] path,
                string[] keys,
                string fallbackIdentifierKey = null,
                Action<JArray, List<JObject>, ArrayMergeRule> newItemHandler = null,
                string[] requiredPropertiesForNewItems = null)
            {
                Path = path;
                Keys = keys;
                FallbackIdentifierKey = fallbackIdentifierKey;
                RequiredPropertiesForNewItems = requiredPropertiesForNewItems ?? new string[0];
                // Default behavior for new items is to simply append them.
                NewItemHandler = newItemHandler ?? ((baseArray, newItems, rule) =>
                {
                    // Before adding, perform the generic validation for required properties.
                    ValidateNewItems(newItems, rule);
                    foreach (var item in newItems) baseArray.Add(item);
                });
            }
        }

        // Centralized configuration for all custom array merging logic.
        private static readonly List<ArrayMergeRule> s_arrayMergeRules = new List<ArrayMergeRule>
        {
            new ArrayMergeRule(
                path: new [] { "SharedObjects" },
                keys: new [] { "$id" },
                newItemHandler: HandleNewItemInsertions,
                requiredPropertiesForNewItems: new[] { "$type" }
            ),
            new ArrayMergeRule(
                path: new [] { "Workers" },
                keys: new [] { "AudioExtractionId", "TranslationId", "TranscriptionId", "OutputId" }, // TranslationId must be before TranscriptionId because of TranslatorGoogleV1API
                fallbackIdentifierKey: "Id",
                newItemHandler: HandleNewItemInsertions,
                requiredPropertiesForNewItems: new[] { "$type" }
            ),
            new ArrayMergeRule(
                path: new [] { "*", "BinaryDataExtractors" },
                keys: new [] { "OutputFieldName" },
                newItemHandler: HandleNewItemInsertions,
                requiredPropertiesForNewItems: new[] { "$type" }
            )
        };
        /// <summary>
        /// The public entry point for merging configurations. It starts the recursive merge process.
        /// </summary>
        /// <param name="baseConfig">The base configuration JObject to merge into.</param>
        /// <param name="userConfig">The user override JObject.</param>
        /// <summary>
        /// Merges an override file, with the name of the file in the error (ex. an item that can't be merged).
        /// (the line numbers are the ones of the file, see PreprocessHybridJsonFile)
        /// </summary>
        private static void MergeConfigFile(JObject baseConfig, JObject userConfig, string path)
        {
            try
            {
                MergeConfigs(baseConfig, userConfig);
            }
            catch (JsonException ex)
            {
                throw new Exception($"Error merging configuration file '{path}': {ex.Message}", ex);
            }
        }

        private static void MergeConfigs(JObject baseConfig, JObject userConfig)
        {
            // Start the recursive merge at the root of the JSON structure (with an empty path).
            MergeConfigs(baseConfig, userConfig, new List<string>());
        }

        /// <summary>
        /// Recursively merges a user override configuration into a base configuration, tracking the
        /// current path to apply rule-based array merges at any depth.
        /// </summary>
        /// <param name="baseConfig">The base JObject for the current level.</param>
        /// <param name="userConfig">The override JObject for the current level.</param>
        /// <param name="currentPath">The path from the root to the current level.</param>
        private static void MergeConfigs(JObject baseConfig, JObject userConfig, List<string> currentPath)
        {
            foreach (var userProp in userConfig.Properties())
            {
                var baseProp = baseConfig.Property(userProp.Name, StringComparison.OrdinalIgnoreCase);
                var newPath = new List<string>(currentPath) { userProp.Name };

                // Find a rule that matches the full path to this property.
                var matchingRule = s_arrayMergeRules.FirstOrDefault(r => IsPathMatch(r.Path, newPath));

                if (baseProp != null && userProp.Value is JArray userArray && baseProp.Value is JArray baseArray && matchingRule != null)
                {
                    // --- Custom Array Merge ---
                    // A rule was found for this array, so merge its items based on the rule's keys.
                    MergeArraysByKey(baseArray, userArray, matchingRule, newPath);
                }
                else if (baseProp != null && userProp.Value is JObject userObject && baseProp.Value is JObject baseObject)
                {
                    if (IsAnotherObject(baseObject, userObject))
                    {
                        // --- Object Replaced ---
                        // The override points to another object (another '$ref', or another '$type'): its properties replace the
                        // whole object (the properties of the old one would not apply to it).
                        var baseId = baseObject.Property("$id");
                        if (baseId != null)
                        {
                            throw new Exception(
                                $"'{string.Join(".", newPath)}' defines the object '$id': '{baseId.Value}' (it can be used elsewhere with a '$ref'): " +
                                "it can't be replaced by another '$ref' or '$type'. Change its properties instead, or define it in 'SharedObjects'.");
                        }
                        var oldValue = baseProp.Value;
                        baseProp.Value = userObject;
                        RecordOverridden(oldValue, baseProp.Value, userObject);
                    }
                    else
                    {
                        // --- Recursive Object Merge ---
                        // This property is an object, so we continue the merge recursively, passing the updated path.
                        MergeConfigs(baseObject, userObject, newPath);
                    }
                }
                else
                {
                    // --- Simple Value Merge ---
                    // This is a simple value (string, int, etc.) or a property new to the base config.
                    // We can add or replace it directly.
                    if (baseProp != null)
                    {
                        var oldValue = baseProp.Value;
                        baseProp.Value = userProp.Value;
                        RecordOverridden(oldValue, baseProp.Value, userProp.Value);
                    }
                    else
                    {
                        baseConfig.Add(userProp);
                    }
                }
            }
        }

        /// <summary>
        /// True when the override is another object than the base, and replaces it instead of being merged in it:
        /// another '$ref', a '$ref' over an object defined in place (or the reverse), or another '$type'.
        /// The same '$ref' (ex. { "$ref": "X", "Runs": 3 } then { "Runs": 4 }) or no '$ref' / '$type' in the override: merged.
        /// </summary>
        private static bool IsAnotherObject(JObject baseObject, JObject userObject)
        {
            var userRef = (string)userObject["$ref"];
            var baseRef = (string)baseObject["$ref"];
            if (userRef != null)
                return !string.Equals(userRef, baseRef, StringComparison.Ordinal);

            var userType = (string)userObject["$type"];
            if (userType != null)
                return baseRef != null || !string.Equals(userType, (string)baseObject["$type"], StringComparison.Ordinal);

            return false;
        }

        /// <summary>
        /// A '$ref' with other properties (ex. "Engine": { "$ref": "TimingRefineEngineSpokenMarkers", "Runs": 3 }): replaced by a copy of
        /// the object it points to, with these properties merged in it (same rules as an override file). The shared object itself is not
        /// changed (the other '$ref' to it are not affected). Done once, after all the override files are merged, so the copy has the final
        /// values of the shared object. (Newtonsoft.Json refuses a '$ref' with other properties.)
        /// </summary>
        private static void ExpandReferencesWithOverrides(JObject root)
        {
            var definitions = root.DescendantsAndSelf()
                .OfType<JObject>()
                .Where(o => o.Property("$id")?.Value.Type == JTokenType.String)
                .GroupBy(o => (string)o["$id"], StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            ExpandReferencesWithOverrides(root, definitions, new List<string>(), new List<string>());
        }

        private static void ExpandReferencesWithOverrides(
            JToken token,
            Dictionary<string, List<JObject>> definitions,
            List<string> path,
            List<string> expanding)
        {
            if (token is JArray array)
            {
                // (the items of an array have the path of the array, like in MergeArraysByKey)
                foreach (var item in array.ToList())
                    ExpandReferencesWithOverrides(item, definitions, path, expanding);
                return;
            }
            if (!(token is JObject obj))
                return;

            var referenceProp = obj.Property("$ref");
            var overrides = obj.Properties().Where(p => p.Name != "$ref").ToList();
            if (referenceProp != null && overrides.Count > 0)
            {
                var id = (string)referenceProp.Value;
                var where = path.Count == 0 ? "(root)" : string.Join(".", path);
                if (id == null)
                    throw new Exception($"'{where}': '$ref' must be the name of an object ('$id').");
                if (expanding.Contains(id, StringComparer.Ordinal))
                    throw new Exception($"'{where}': the object '{id}' contains a '$ref' to itself with other properties ({string.Join(" -> ", expanding.Append(id))}).");
                if (!definitions.TryGetValue(id, out var candidates))
                    throw new Exception($"'{where}': '$ref' to '{id}' with other properties, but no object has '$id': '{id}'.");
                if (candidates.Count > 1)
                    throw new Exception($"'{where}': '$ref' to '{id}' with other properties, but {candidates.Count} objects have '$id': '{id}'.");

                // A copy of the shared object (without any '$id': they are defined once, by the original), then the properties given with the '$ref'
                var expanded = (JObject)candidates[0].DeepClone();
                foreach (var idProp in expanded.DescendantsAndSelf().OfType<JObject>().Select(o => o.Property("$id")).Where(p => p != null).ToList())
                    idProp.Remove();
                MergeConfigs(expanded, new JObject(overrides.Select(p => new JProperty(p.Name, p.Value.DeepClone()))), path);

                // ('$type' must be the first property, to be read as the type of the object)
                var typeProp = expanded.Property("$type");
                if (typeProp != null && expanded.Properties().First() != typeProp)
                {
                    typeProp.Remove();
                    expanded.AddFirst(typeProp);
                }

                // (the sources are tracked: the copy knows which shared object it is, and where the '$ref' was written)
                var referenceSource = referenceProp.Value.Annotation<ConfigSource>();
                if (referenceSource != null)
                    expanded.AddAnnotation(new ConfigExpandedReference(id, referenceSource));

                obj.Replace(expanded);
                expanding.Add(id);
                ExpandReferencesWithOverrides(expanded, definitions, path, expanding);
                expanding.RemoveAt(expanding.Count - 1);
                return;
            }

            foreach (var prop in obj.Properties().ToList())
                ExpandReferencesWithOverrides(prop.Value, definitions, new List<string>(path) { prop.Name }, expanding);
        }

        /// <summary>
        /// Checks if an actual JSON path matches a rule's path, supporting wildcards.
        /// The match is performed from the end of the path backwards.
        /// </summary>
        /// <param name="rulePath">The path defined in the rule (e.g., ["*", "BinaryDataExtractors"]).</param>
        /// <param name="actualPath">The full path to the property being checked (e.g., ["Workers", "Options", "BinaryDataExtractors"]).</param>
        /// <returns>True if the path matches the rule.</returns>
        private static bool IsPathMatch(string[] rulePath, List<string> actualPath)
        {
            // The actual path must be at least as long as the rule's path.
            if (actualPath.Count < rulePath.Length)
            {
                return false;
            }

            int ruleIndex = rulePath.Length - 1;
            int actualIndex = actualPath.Count - 1;

            // Compare backwards from the end of both paths.
            while (ruleIndex >= 0)
            {
                if (rulePath[ruleIndex] != "*" && !string.Equals(rulePath[ruleIndex], actualPath[actualIndex], StringComparison.OrdinalIgnoreCase))
                {
                    return false; // Segments don't match, and it's not a wildcard.
                }

                ruleIndex--;
                actualIndex--;
            }

            return true; // All segments of the rule path were matched.
        }


        /// <summary>
        /// Performs a generic, key-based merge of two JArrays based on a provided merge rule.
        /// </summary>
        /// <param name="baseArray">The base array to merge into.</param>
        /// <param name="userArray">The override array.</param>
        /// <param name="rule">The rule defining how to match items.</param>
        /// <param name="arrayPath">The path to this array, to be passed for recursive merges.</param>
        private static void MergeArraysByKey(JArray baseArray, JArray userArray, ArrayMergeRule rule, List<string> arrayPath)
        {
            var newItems = new List<JObject>();

            foreach (var userItemToken in userArray)
            {
                if (!(userItemToken is JObject userItem)) continue; // Skip non-object items.

                JObject baseItemToUpdate = FindMatchingItem(baseArray, userItem, rule);

                if (baseItemToUpdate != null)
                {
                    // Found a matching item in the base array. Recursively merge its properties.
                    // This is crucial for handling nested rules, like BinaryDataExtractors inside a Worker.
                    MergeConfigs(baseItemToUpdate, userItem, arrayPath);
                }
                else
                {
                    // This is a new item that doesn't exist in the base array.
                    newItems.Add(userItem);
                }
            }

            // Use the rule's specified handler to add the new items to the base array.
            rule.NewItemHandler(baseArray, newItems, rule);
        }
        /// <summary>
        /// Finds an item in a base array that matches a user-provided item, based on the keys defined in a rule.
        /// </summary>
        private static JObject FindMatchingItem(JArray baseArray, JObject userItem, ArrayMergeRule rule)
        {
            // If a FallbackIdentifierKey is defined (like "Id" for Workers), get its value.
            var fallbackIdValue = !string.IsNullOrEmpty(rule.FallbackIdentifierKey)
                ? userItem[rule.FallbackIdentifierKey]?.ToString()
                : null;

            // The fallback key from a user item (e.g., "Id") should not be part of the final config,
            // so we remove it after getting its value.
            if (fallbackIdValue != null)
            {
                userItem.Remove(rule.FallbackIdentifierKey);
            }

            foreach (var baseItemToken in baseArray)
            {
                if (!(baseItemToken is JObject baseItem)) continue;

                var baseItemId = GetItemId(baseItem, rule);
                if (baseItemId == null) continue;

                // Check each key defined in the rule to see if we have a match.
                foreach (var key in rule.Keys)
                {
                    // Use the fallback ID if the specific key (e.g., "AudioExtractionId") is missing from the user item.
                    var userValue = userItem[key]?.ToString() ?? fallbackIdValue;
                    if (userValue != null && string.Equals(userValue, baseItemId, StringComparison.OrdinalIgnoreCase))
                    {
                        return baseItem; // Found a match.
                    }
                }
            }

            return null; // No match found.
        }

        /// <summary>
        /// Gets the unique identifier of an item within an array according to the merge rule.
        /// It checks each key specified in the rule and returns the first value it finds.
        /// </summary>
        private static string GetItemId(JObject item, ArrayMergeRule rule)
        {
            foreach (var key in rule.Keys)
            {
                var id = item[key]?.ToString();
                if (!string.IsNullOrEmpty(id))
                {
                    return id;
                }
            }
            // Check the fallback key as a last resort (primarily for base items).
            if (!string.IsNullOrEmpty(rule.FallbackIdentifierKey))
            {
                return item[rule.FallbackIdentifierKey]?.ToString();
            }
            return null;
        }

        /// <summary>
        /// Validates that new items being added to an array contain all required properties as defined by a rule.
        /// </summary>
        private static void ValidateNewItems(List<JObject> newItems, ArrayMergeRule rule)
        {
            if (rule.RequiredPropertiesForNewItems.Length == 0) return;

            foreach (var newItem in newItems)
            {
                foreach (var requiredProp in rule.RequiredPropertiesForNewItems)
                {
                    if (newItem[requiredProp] == null)
                    {
                        string errorContext = newItem.ToString(Formatting.None);
                        // (the usual cause: the item was meant to change an existing one, but its key is missing, commented or misspelled)
                        var keys = string.Join(" / ", rule.Keys.Concat(rule.FallbackIdentifierKey == null ? Array.Empty<string>() : new[] { rule.FallbackIdentifierKey }).Select(k => $"'{k}'"));
                        var hint = $"An item of '{string.Join(".", rule.Path)}' without {keys} matching an existing item is a new item. "
                            + $"If it's meant to change an existing item, its {keys} is missing, commented or misspelled.";
                        if (newItem is IJsonLineInfo lineInfo && lineInfo.HasLineInfo())
                        {
                            throw new JsonException($"A new item is missing the required '{requiredProp}' property. The item starts on line {lineInfo.LineNumber}:\n{errorContext}\n{hint}");
                        }
                        throw new JsonException($"A new item is missing the required '{requiredProp}' property:\n{errorContext}\n{hint}");
                    }
                }
            }
        }

        /// <summary>
        /// A generic method to insert new items into a base array. It supports insertion relative to existing
        /// items using '$insertBefore' and '$insertAfter' meta-properties.
        /// </summary>
        private static void HandleNewItemInsertions(JArray baseArray, List<JObject> newItems, ArrayMergeRule rule)
        {
            // 1. First, validate all new items to ensure they meet the rule's requirements.
            ValidateNewItems(newItems, rule);

            var itemsToAppend = new List<JObject>();
            // A list of items to insert, paired with their target ID and whether to insert before or after.
            var pendingInsertions = new List<(JObject item, string targetId, bool insertAfter)>();

            foreach (var newItem in newItems)
            {
                var insertBefore = newItem.GetValue("$insertBefore", StringComparison.OrdinalIgnoreCase);
                var insertAfter = newItem.GetValue("$insertAfter", StringComparison.OrdinalIgnoreCase);

                newItem.Remove("$insertBefore");
                newItem.Remove("$insertAfter");

                if (insertBefore != null)
                {
                    pendingInsertions.Add((newItem, insertBefore.ToString(), false));
                }
                else if (insertAfter != null)
                {
                    pendingInsertions.Add((newItem, insertAfter.ToString(), true));
                }
                else
                {
                    itemsToAppend.Add(newItem);
                }
            }

            if (pendingInsertions.Count == 0)
            {
                itemsToAppend.ForEach(i => baseArray.Add(i));
                return;
            }

            // 2. Iterate backwards through the base array to handle insertions without shifting indices incorrectly.
            for (int i = baseArray.Count - 1; i >= 0; i--)
            {
                if (!(baseArray[i] is JObject baseItem)) continue;

                var baseItemId = GetItemId(baseItem, rule);
                if (baseItemId == null) continue;

                // Find all items that should be inserted relative to the current baseItem.
                var insertionsForThisItem = pendingInsertions.Where(p => string.Equals(p.targetId, baseItemId, StringComparison.OrdinalIgnoreCase)).ToList();
                if (insertionsForThisItem.Count == 0) continue;

                // To preserve the order from the override file, we process the found items in reverse.
                for (int j = insertionsForThisItem.Count - 1; j >= 0; j--)
                {
                    var pending = insertionsForThisItem[j];
                    if (pending.insertAfter)
                    {
                        baseArray.Insert(i + 1, pending.item);
                    }
                    else // insertBefore
                    {
                        baseArray.Insert(i, pending.item);
                    }
                    // Remove the processed item so it doesn't get appended later.
                    pendingInsertions.Remove(pending);
                }
            }

            // 3. Any remaining pending insertions had a targetId that was not found. Append them.
            foreach (var remaining in pendingInsertions)
            {
                itemsToAppend.Add(remaining.item);
            }

            // 4. Append all remaining items to the end of the array.
            foreach (var item in itemsToAppend)
            {
                baseArray.Add(item);
            }
        }

        const string STARTLONGSTRING = "=_______________________________________";
        const string ENDLONGSTRING   = "_______________________________________=";

        public static string CreateLongString(string text)
        {
            return STARTLONGSTRING + text + ENDLONGSTRING;
        }

        /// <summary>
        /// The hybrid text as JSON. The lines are kept where they are (a comment line becomes an empty line, a long string is followed by
        /// as many line breaks as it had), so the line numbers of an error are the ones of the file.
        /// </summary>
        private static string PreprocessHybridJsonFile(string originalText)
        {
            var textWithoutComment = Regex.Replace(originalText, @"^[ \t]*\/\/[^\n]*?(?=\r?$)", string.Empty, RegexOptions.Multiline);
            return ReplaceLongStringFromHybridToJson(textWithoutComment);
        }

        public static string ReplaceLongStringFromJsonToHybrid(string originalJson)
        {
            return Regex.Replace(
                originalJson,
                @"=_{3,}(?<text>.*?)_{3,}=",
                match => {
                    // Extract the captured text. This is the content of the JSON string.
                    string capturedText = match.Groups["text"].Value;

                    // To properly deserialize the string content, we must wrap it in quotes
                    // to make it a valid JSON string literal.
                    string jsonStringLiteral = "\"" + capturedText + "\"";

                    // Use Newtonsoft.Json to deserialize the string.
                    // This correctly handles all JSON escape sequences like \", \n, \r, \\, \t, etc.
                    string decodedText = JsonConvert.DeserializeObject<string>(jsonStringLiteral);

                    // Construct the final replacement string.
                    return "\n" + STARTLONGSTRING + "\n\n" + decodedText.Trim() + "\n\n" + ENDLONGSTRING;
                });
        }

        private static string ReplaceLongStringFromHybridToJson(string originalText)
        {
            return Regex.Replace(
                originalText,
                @"\n*=_{3,}(?<text>.*?)_{3,}=(?:\n*[ \t]*(?<quote>"")|\n*)",
                match =>
                {
                    string capturedText = match.Groups["text"].Value.Trim();

                    // Serialize the string to correctly escape all necessary characters.
                    // This will also wrap the string in double quotes.
                    string jsonString = JsonConvert.SerializeObject(capturedText);

                    // Remove the surrounding quotes added by the serializer
                    // to get just the escaped content.
                    // (after the closing quote of the string: the line breaks it had, so the next lines keep their number)
                    var quote = match.Groups["quote"].Value;
                    var lineBreaks = quote.Length == 0 ? string.Empty : new string('\n', match.Value.Count(c => c == '\n'));
                    return jsonString.Substring(1, jsonString.Length - 2) + quote + lineBreaks;
                },
                RegexOptions.Singleline);
        }

        public static string GetMinimalFileContent(
                    SubtitleGeneratorConfig originalConfig,
                    bool removeUnusedTasks = true,
                    bool removeUnusedSharedObjects = true)
        {
            // 1. Serialize the full current configuration to a JObject
            var rootConfig = JObject.FromObject(originalConfig, rs_serializer);

            var workersArray = rootConfig[nameof(SubtitleGeneratorConfig.Workers)] as JArray;
            var sharedObjectsArray = rootConfig[nameof(SubtitleGeneratorConfig.SharedObjects)] as JArray;

            // 2. Filter out disabled workers (only if requested)
            if (removeUnusedTasks && workersArray != null)
            {
                var workersToRemove = workersArray
                    .Where(w =>
                    {
                        var enabledToken = w[nameof(SubtitleWorker.Enabled)];
                        return enabledToken == null || enabledToken.Value<bool>() == false;
                    })
                    .ToList();

                foreach (var w in workersToRemove)
                {
                    w.Remove();
                }
            }

            var neededIds = new HashSet<string>();
            var idMap = new Dictionary<string, string>();
            var refContext = new Dictionary<string, (string WorkerId, string PropertyName)>();

            // 3. Find and filter the required SharedObjects based on reference mapping
            if (sharedObjectsArray != null && workersArray != null)
            {
                var queue = new Queue<(string RefId, string WorkerId, string PropertyName)>();

                // Get initial required references from all active workers
                foreach (var worker in workersArray)
                {
                    string workerId = GetWorkerId(worker);
                    foreach (var (refId, propName) in GetRefs(worker))
                    {
                        queue.Enqueue((refId, workerId, propName));
                    }
                }

                // 4. Iteratively resolve dependencies in SharedObjects
                while (queue.Count > 0)
                {
                    var (id, workerId, propName) = queue.Dequeue();

                    if (neededIds.Add(id))
                    {
                        refContext[id] = (workerId, propName);

                        var sharedObj = sharedObjectsArray.FirstOrDefault(o => o["$id"]?.Value<string>() == id);
                        if (sharedObj != null)
                        {
                            foreach (var (nestedRef, nestedProp) in GetRefs(sharedObj))
                            {
                                if (!neededIds.Contains(nestedRef))
                                {
                                    // Pass down the same workerId, but use the new nested property name
                                    queue.Enqueue((nestedRef, workerId, nestedProp));
                                }
                            }
                        }
                    }
                }

                var usedNames = new HashSet<string>();

                // 5. Process SharedObjects: Remove unused, rename intelligently, format prompts
                var objectsToRemove = sharedObjectsArray
                    .Where(o =>
                    {
                        var oldId = o["$id"]?.Value<string>();
                        bool isReferenced = oldId != null && neededIds.Contains(oldId);

                        if (removeUnusedSharedObjects && !isReferenced)
                        {
                            return true; // Remove it
                        }

                        if (oldId != null && !isReferenced)
                        {
                            neededIds.Add(oldId);
                        }

                        var typeName = o["$type"]?.Value<string>() ?? nameof(SubtitleGeneratorConfig.SharedObjects);
                        string baseId = typeName;

                        // --- AIPrompt Specific Naming and Formatting strongly typed ---
                        if (typeName == nameof(AIPrompt))
                        {
                            if (oldId != null && refContext.TryGetValue(oldId, out var context))
                            {
                                // Look at the property name to see if it matches SystemPrompt or UserPrompt
                                if (context.PropertyName.Contains(nameof(AIOptions.SystemPrompt)))
                                {
                                    // Generates "AISystemPrompt"
                                    baseId = $"AI{nameof(AIOptions.SystemPrompt)}";
                                }
                                else if (context.PropertyName.Contains(nameof(AIOptions.UserPrompt)))
                                {
                                    // Generates "AIUserPrompt"
                                    baseId = $"AI{nameof(AIOptions.UserPrompt)}";
                                }

                                if (!string.IsNullOrEmpty(context.WorkerId))
                                {
                                    baseId = $"{baseId}_{context.WorkerId}";
                                }
                            }

                            string[] textFields = {
                                nameof(AIPrompt.Text),
                                nameof(AIPrompt.TextBefore),
                                nameof(AIPrompt.TextAfter)
                            };

                            foreach (var field in textFields)
                            {
                                var token = o[field];
                                if (token != null && token.Type == JTokenType.String)
                                {
                                    string val = token.Value<string>();
                                    if (!string.IsNullOrEmpty(val) && !val.StartsWith(STARTLONGSTRING))
                                    {
                                        o[field] = CreateLongString(val);
                                    }
                                }
                            }
                        }

                        string newId = baseId;
                        int counter = 2;

                        // Ensure name is strictly unique
                        while (usedNames.Contains(newId))
                        {
                            newId = $"{baseId}_{counter}";
                            counter++;
                        }

                        usedNames.Add(newId);

                        if (oldId != null)
                        {
                            idMap[oldId] = newId;
                        }

                        return false; // Don't remove it
                    })
                    .ToList();

                foreach (var o in objectsToRemove)
                {
                    o.Remove();
                }
            }

            // 5.5 Clean up ExpandoObject metadata and unwrap $values collections
            CleanDynamicAndCollectionTypes(rootConfig);

            // 6. Strip unreferenced IDs and rename the needed ones (and their references)
            CleanupAndRenameIds(rootConfig, neededIds, idMap);

            // 7. Convert back to string and apply the hybrid long-string visual formatting
            string jsonContent = rootConfig.ToString(Formatting.Indented);
            return ReplaceLongStringFromJsonToHybrid(jsonContent);
        }

        /// <summary>
        /// Extracts the most logical identifier from a Worker object using strongly-typed property names.
        /// </summary>
        private static string GetWorkerId(JToken workerToken)
        {
            if (workerToken is JObject workerObj)
            {
                // Strongly typed keys resilient to refactoring
                string[] idKeys = {
                    nameof(Translator.TranslationId),
                    nameof(Transcriber.TranscriptionId),
                    nameof(AudioExtractor.AudioExtractionId),
                    nameof(SubtitleOutput.OutputId),
                    "Id" // Kept as string literal since it's an abstract fallback concept in your array merge rule
                };

                foreach (var key in idKeys)
                {
                    var val = workerObj[key]?.Value<string>();
                    if (!string.IsNullOrEmpty(val))
                    {
                        return val;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Recursively removes "$id" properties that aren't needed, and renames needed "$id"s and "$ref"s.
        /// </summary>
        private static void CleanupAndRenameIds(JToken token, HashSet<string> neededIds, Dictionary<string, string> idMap)
        {
            if (token is JObject obj)
            {
                // Handle $id
                var idToken = obj["$id"];
                if (idToken != null)
                {
                    var oldId = idToken.Value<string>();
                    if (!neededIds.Contains(oldId))
                    {
                        obj.Remove("$id");
                    }
                    else if (idMap.TryGetValue(oldId, out string descriptiveName))
                    {
                        obj["$id"] = descriptiveName;
                    }
                }

                // Handle $ref
                var refToken = obj["$ref"];
                if (refToken != null)
                {
                    var oldRef = refToken.Value<string>();
                    if (idMap.TryGetValue(oldRef, out string descriptiveName))
                    {
                        obj["$ref"] = descriptiveName;
                    }
                }

                // Recurse (using ToList() to avoid modification exceptions)
                foreach (var prop in obj.Properties().ToList())
                {
                    CleanupAndRenameIds(prop.Value, neededIds, idMap);
                }
            }
            else if (token is JArray arr)
            {
                foreach (var item in arr)
                {
                    CleanupAndRenameIds(item, neededIds, idMap);
                }
            }
        }

        /// <summary>
        /// Recursively removes JSON.NET type metadata ($type and $values) from dynamic objects 
        /// and generic collections to produce clean JSON output.
        /// </summary>
        private static void CleanDynamicAndCollectionTypes(JToken token)
        {
            if (token is JObject obj)
            {
                // Process children bottom-up first so replacements don't break our traversal
                foreach (var prop in obj.Properties().ToList())
                {
                    CleanDynamicAndCollectionTypes(prop.Value);
                }

                var typeToken = obj["$type"];
                if (typeToken != null)
                {
                    string typeValue = typeToken.Value<string>();
                    var valuesToken = obj["$values"];

                    if (valuesToken != null && obj.Parent != null)
                    {
                        // It's a wrapped collection (e.g., List<object>). 
                        // Replace the metadata wrapper object directly with the array.
                        obj.Replace(valuesToken);
                    }
                    else if (typeValue != null && typeValue.Contains("System.Dynamic.ExpandoObject"))
                    {
                        // It's a dynamic dictionary. Just remove the type property.
                        obj.Remove("$type");
                    }
                }
            }
            else if (token is JArray arr)
            {
                foreach (var item in arr.ToList())
                {
                    CleanDynamicAndCollectionTypes(item);
                }
            }
        }

        /// <summary>
        /// Recursively searches the JToken hierarchy for "$ref" properties and extracts their values alongside the property name that holds them.
        /// </summary>
        private static IEnumerable<(string RefId, string PropertyName)> GetRefs(JToken token)
        {
            var refs = new List<(string, string)>();
            GetRefsRecursive(token, refs);
            return refs;
        }

        private static void GetRefsRecursive(JToken token, List<(string, string)> refs)
        {
            if (token is JObject obj)
            {
                if (obj.TryGetValue("$ref", out var refToken))
                {
                    string propName = (obj.Parent as JProperty)?.Name ?? "UnknownProperty";
                    refs.Add((refToken.Value<string>(), propName));
                }

                foreach (var prop in obj.Properties())
                {
                    GetRefsRecursive(prop.Value, refs);
                }
            }
            else if (token is JArray arr)
            {
                foreach (var item in arr)
                {
                    GetRefsRecursive(item, refs);
                }
            }
        }

        internal class ValidatingReferenceResolver : IReferenceResolver
        {
            private readonly IReferenceResolver r_parent;

            public ValidatingReferenceResolver(IReferenceResolver parentResolver)
            {
                r_parent = parentResolver;
            }

            public void AddReference(object context, string reference, object value) => r_parent.AddReference(context, reference, value);
            public string GetReference(object context, object value) => r_parent.GetReference(context, value);
            public bool IsReferenced(object context, object value) => r_parent.IsReferenced(context, value);
            public object ResolveReference(object context, string reference) => r_parent.ResolveReference(context, reference) 
                    ?? throw new Exception($"Reference '{reference}' cannot be resolved.");
        }

        public class JTokenIdOverride
        { 
            public string TypeName { get; }
            public string NewId { get; }
            public string OldId { get; set; }

            public JTokenIdOverride(string typeName, string newId)
            {
                TypeName = typeName;
                NewId = newId;
                OldId = null;
            }
        }

        static public JToken OverridesIdInJObject(JToken token, List<JTokenIdOverride> replacements)
            => OverridesIdInJObject(token, replacements, "$", null);

        static public JToken OverridesIdInJObject(
            JToken token,
            List<JTokenIdOverride> replacements,
            string path,
            Action<string> log)
        {
            try
            {
                if (token is JObject obj)
                {
                    // Log node enter (optional, might be chatty)
                    // log?.Invoke($"Visiting {path} (type={obj["$type"]?.ToString() ?? "n/a"})");

                    var type = (string)obj["$type"];
                    if (type?.StartsWith("<>") == true)
                    {
                        log?.Invoke($"[{path}] Removing compiler-generated $type '{type}'.");
                        obj.Remove("$type");
                        type = null;
                    }

                    // Replace $id if it matches a first-time TypeName
                    var match = replacements.FirstOrDefault(t => t.TypeName == type && t.OldId == null);
                    if (match != null)
                    {
                        match.OldId = (string)obj["$id"];
                        var oldId = match.OldId ?? "(null)";
                        obj["$id"] = match.NewId;
                        log?.Invoke($"[{path}] Overriding $id: {oldId} -> {match.NewId} for type '{type}'.");
                    }
                    else
                    {
                        if (obj.Property("$id") != null)
                        {
                            log?.Invoke($"[{path}] Removing unneeded $id '{obj["$id"]}'.");
                            obj.Remove("$id");
                        }
                    }

                    // Resolve $ref
                    var reference = (string)obj["$ref"];
                    if (reference != null)
                    {
                        var byOldId = replacements.FirstOrDefault(t => t.OldId == reference);
                        if (byOldId != null)
                        {
                            log?.Invoke($"[{path}] Rewriting $ref: {reference} -> {byOldId.NewId}.");
                            obj["$ref"] = byOldId.NewId;
                        }
                        else
                        {
                            var message = $"Unresolvable $ref '{reference}' at {path} (token.Path='{token.Path}'). " +
                                          "No matching OldId recorded. " +
                                          "Hints: verify SharedObjects order and that all owners with $id were visited before $ref nodes.";
                            throw new Exception(message);
                        }
                    }

                    // Recurse into properties
                    foreach (var property in obj.Properties().ToList())
                    {
                        var childPath = path + "." + property.Name;
                        OverridesIdInJObject(property.Value, replacements, childPath, log);
                    }
                }
                else if (token is JArray array)
                {
                    for (int i = 0; i < array.Count; i++)
                    {
                        var childPath = $"{path}[{i}]";
                        OverridesIdInJObject(array[i], replacements, childPath, log);
                    }
                }
                // JValue: nothing to do
                return token;
            }
            catch (Exception ex)
            {
                // Wrap to add path context if not already present
                throw new Exception($"While processing {path} (token.Path='{token.Path}'):\n{ex.Message}", ex);
            }
        }
    }
}