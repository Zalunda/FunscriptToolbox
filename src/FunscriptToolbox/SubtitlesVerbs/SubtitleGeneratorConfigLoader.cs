using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.AudioExtractions;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using FunscriptToolbox.SubtitlesVerbs.Outputs;
using FunscriptToolbox.SubtitlesVerbs.Transcriptions;
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
        public static readonly JsonSerializer rs_serializer = JsonSerializer
                .Create(new JsonSerializerSettings
                {
                    Formatting = Formatting.Indented,
                    SerializationBinder = new SimpleTypeNameSerializationBinder(
                        new[] {
                            typeof(AIEngine),
                            typeof(AIOptions),
                            typeof(AIPrompt),
                            typeof(AudioExtractor),
                            typeof(SubtitleOutput),
                            typeof(TranscriberToolAudio),
                            typeof(Transcriber),
                            typeof(Translator)
                        }),
                    TypeNameHandling = TypeNameHandling.Auto,
                    PreserveReferencesHandling = PreserveReferencesHandling.Objects
                });
        static SubtitleGeneratorConfigLoader()
        {
            rs_serializer.Converters.Add(new StringEnumConverter());
            rs_serializer.ReferenceResolver = new ValidatingReferenceResolver(rs_serializer.ReferenceResolver);
        }
        // This pattern looks for two letters inside square brackets, e.g., "[ja]".
        private static readonly Regex s_languageInFolderNameRegex = new Regex(@"\[(?<name>[a-zA-Z]{2})\]", RegexOptions.Compiled);

        /// <summary>
        /// Loads a base configuration and merges all override files found in the path
        /// leading from the base config's directory down to the target file's directory.
        /// </summary>
        /// <param name="baseConfigPath">The full path to the main configuration file.</param>
        /// <param name="targetFileFullPath">The full path of the video/wipsub file being processed.</param>
        /// <returns>A fully resolved and merged SubtitleGeneratorConfig object.</returns>
        public static SubtitleGeneratorConfig LoadHierarchically(string baseConfigPath, string targetFileFullPath)
        {
            // Helper function to read and parse a config file.
            static JObject ReadHybridJsonFile(string path)
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
                    var adjustedFileName = path + ".as.json";
                    File.WriteAllText(adjustedFileName, "ERROR:\n" + ex.Message + "\n\n--- JSON CONTENT ---\n" + adjustedContent);
                    throw new Exception($"Error parsing configuration file '{path}'. Details written to '{adjustedFileName}'.\n\nDetails: {ex.Message}", ex);
                }
            }

            // 1. Load the base configuration file.
            var finalConfig = ReadHybridJsonFile(baseConfigPath);
            var lastFullPath = baseConfigPath;

            // 2. Determine paths and the name for override files.
            var baseConfigDir = Path.GetFullPath(Path.GetDirectoryName(baseConfigPath));
            var targetFileDir = Path.GetFullPath(Path.GetDirectoryName(targetFileFullPath));

            // The name of the file that acts as a new root/base for a subtree.
            var rootConfigFileName = Path.GetFileName(baseConfigPath);
            var overrideConfigFileName = Path.GetFileNameWithoutExtension(baseConfigPath) + ".override.config";

            // 3. Ensure the target file is in a subdirectory of the base config.
            if (!targetFileDir.StartsWith(baseConfigDir + @"\", StringComparison.OrdinalIgnoreCase))
            {
                // If not, no overrides can be applied. Just return the base config.
                return finalConfig.ToObject<SubtitleGeneratorConfig>(rs_serializer);
            }

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
            foreach (var dirPath in directoriesToScan)
            {
                var dirInfo = new DirectoryInfo(dirPath);

                // Check the directory name for a language code like "[ja]".
                var languageMatch = s_languageInFolderNameRegex.Match(dirInfo.Name);
                if (languageMatch.Success)
                {
                    string langCode = languageMatch.Groups["name"].Value;
                    // Validate that it's a real language.
                    if (Language.FromString(langCode) != null)
                    {
                        // It's valid, so override the "SourceLanguage" property in our JObject.
                        finalConfig["SourceLanguage"] = langCode;
                    }
                }

                var rootConfigPath = Path.Combine(dirPath, rootConfigFileName);
                if (File.Exists(rootConfigPath))
                {
                    // Replace the config for the one we just found.
                    finalConfig = ReadHybridJsonFile(rootConfigPath);
                    lastFullPath = rootConfigPath;
                }

                // Now, check for an override file in the same directory.
                // This allows the file to override the folder-name shortcut if needed.
                var overrideConfigPath = Path.Combine(dirPath, overrideConfigFileName);
                if (File.Exists(overrideConfigPath))
                {
                    var overrideConfig = ReadHybridJsonFile(overrideConfigPath);
                    lastFullPath = overrideConfigPath;
                    MergeConfigs(finalConfig, overrideConfig);
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
                    finalConfig["SourceLanguage"] = langCode;
                }
            }

            // 7. Deserialize the final JObject into a config object.
            var finalConfigText = finalConfig.ToString();
            try
            {
                return rs_serializer.Deserialize<SubtitleGeneratorConfig>(
                    new JsonTextReader(
                        new StringReader(finalConfigText)));
            }
            catch (JsonException ex)
            {
                var adjustedFileName = lastFullPath + ".as.json";
                File.WriteAllText(adjustedFileName, "ERROR:\n" + ex.Message + "\n\n--- JSON CONTENT ---\n" + finalConfigText);
                throw new Exception($"Error during final deserialization of merged config for '{lastFullPath}'. Details written to '{adjustedFileName}'.\n\nDetails: {ex.Message}", ex);
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
                    // --- Recursive Object Merge ---
                    // This property is an object, so we continue the merge recursively, passing the updated path.
                    MergeConfigs(baseObject, userObject, newPath);
                }
                else
                {
                    // --- Simple Value Merge ---
                    // This is a simple value (string, int, etc.) or a property new to the base config.
                    // We can add or replace it directly.
                    if (baseProp != null)
                    {
                        baseProp.Value = userProp.Value;
                    }
                    else
                    {
                        baseConfig.Add(userProp);
                    }
                }
            }
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
                        if (newItem is IJsonLineInfo lineInfo && lineInfo.HasLineInfo())
                        {
                            throw new JsonException($"A new item is missing the required '{requiredProp}' property. The item starts on line {lineInfo.LineNumber}:\n{errorContext}");
                        }
                        throw new JsonException($"A new item is missing the required '{requiredProp}' property:\n{errorContext}");
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

        private static string PreprocessHybridJsonFile(string originalText)
        {
            var textWithoutComment = Regex.Replace(originalText, @"^\s*\/\/.*$\n*", string.Empty, RegexOptions.Multiline);
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
                @"\n*=_{3,}(?<text>.*?)_{3,}=\n*",
                match =>
                {
                    string capturedText = match.Groups["text"].Value.Trim();

                    // Serialize the string to correctly escape all necessary characters.
                    // This will also wrap the string in double quotes.
                    string jsonString = JsonConvert.SerializeObject(capturedText);

                    // Remove the surrounding quotes added by the serializer
                    // to get just the escaped content.
                    return jsonString.Substring(1, jsonString.Length - 2);
                },
                RegexOptions.Singleline);
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