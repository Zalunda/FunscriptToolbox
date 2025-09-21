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
                    return JObject.Parse(adjustedContent);
                }
                catch (JsonSerializationException ex)
                {
                    var adjustedFileName = path + ".as.json";
                    File.WriteAllText(adjustedFileName, "ERROR:\n" + ex.Message + "\n\n--- JSON CONTENT ---\n" + content);
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
            catch (Exception ex)
            {
                var adjustedFileName = lastFullPath + ".as.json";
                File.WriteAllText(adjustedFileName, "ERROR:\n" + ex.Message + "\n\n--- JSON CONTENT ---\n" + finalConfigText);
                throw new Exception($"Error during final deserialization of merged config for '{lastFullPath}'. Details written to '{adjustedFileName}'.\n\nDetails: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Merges a user override configuration into a base configuration, with special handling for arrays
        /// to support older versions of Newtonsoft.Json.
        /// </summary>
        /// <param name="baseConfig">The base configuration JObject to merge into.</param>
        /// <param name="userConfig">The user override JObject.</param>
        private static void MergeConfigs(JObject baseConfig, JObject userConfig)
        {
            // --- Step 1: Merge all non-worker properties ---
            // We clone the user config and remove 'Workers' and 'SharedObjects' so the default merge
            // doesn't corrupt our arrays with its index-based logic.
            var userConfigWithoutArrays = (JObject)userConfig.DeepClone();
            userConfigWithoutArrays.Remove("Workers");
            userConfigWithoutArrays.Remove("SharedObjects");
            baseConfig.Merge(userConfigWithoutArrays, new JsonMergeSettings
            {
                PropertyNameComparison = StringComparison.OrdinalIgnoreCase
            });

            // --- Step 2: Custom, ID-based merge for 'SharedObjects' ---
            MergeSharedObjects(baseConfig, userConfig);
            MergeWorkers(baseConfig, userConfig);
        }

        /// <summary>
        /// Performs a custom merge operation for the 'SharedObjects' array,
        /// matching objects by their '$id' property.
        /// </summary>
        private static void MergeSharedObjects(JObject baseConfig, JObject userConfig)
        {
            if (!(userConfig["SharedObjects"] is JArray userSharedObjects) || !userSharedObjects.HasValues)
            {
                return; // Nothing to merge if the user config has no SharedObjects.
            }

            // Ensure the base config has a SharedObjects array to merge into.
            if (!(baseConfig["SharedObjects"] is JArray baseSharedObjects))
            {
                // If the base doesn't have the array, we can just copy the user's array over.
                baseConfig["SharedObjects"] = userSharedObjects.DeepClone();
                return;
            }

            var newObjectsToAdd = new List<JObject>();

            foreach (var userObjectToken in userSharedObjects)
            {
                if (!(userObjectToken is JObject userObject)) continue;

                JObject baseObjectToUpdate = FindMatchingSharedObject(baseSharedObjects, userObject);

                if (baseObjectToUpdate != null)
                {
                    // Found a match, so merge the user's changes into the existing object.
                    baseObjectToUpdate.Merge(userObject);
                }
                else
                {
                    // No match found. This is a new object to be added to the array.
                    newObjectsToAdd.Add(userObject);
                }
            }

            // Add any completely new objects to the end of the base array.
            foreach (var newObject in newObjectsToAdd)
            {
                baseSharedObjects.Add(newObject);
            }
        }

        /// <summary>
        /// Finds a shared object in the base list that matches a shared object from the user's override file
        /// based on the '$id' property.
        /// </summary>
        private static JObject FindMatchingSharedObject(JArray baseSharedObjects, JObject userObject)
        {
            var userId = userObject["$id"]?.ToString();

            // An ID is required to match an object for merging.
            if (string.IsNullOrEmpty(userId))
            {
                return null;
            }

            foreach (var baseObjectToken in baseSharedObjects)
            {
                if (baseObjectToken is JObject baseObject)
                {
                    var baseId = baseObject["$id"]?.ToString();
                    if (string.Equals(userId, baseId, StringComparison.OrdinalIgnoreCase))
                    {
                        return baseObject; // Found the matching object.
                    }
                }
            }

            return null; // No match found.
        }

        private static void MergeWorkers(JObject baseConfig, JObject userConfig)
        {

            // --- Step 3: Custom, ID-based merge for the 'Workers' array ---
            if (!(userConfig["Workers"] is JArray userWorkers) || !(baseConfig["Workers"] is JArray baseWorkers))
            {
                return; // Nothing to do if one of the configs is missing the Workers array.
            }

            var newWorkersToPlace = new List<JObject>();

            foreach (var userWorkerToken in userWorkers)
            {
                if (!(userWorkerToken is JObject userWorker)) continue;

                JObject baseWorkerToUpdate = FindMatchingWorker(baseWorkers, userWorker);

                if (baseWorkerToUpdate != null)
                {
                    // We found a match, so merge the user's changes into the existing worker.
                    baseWorkerToUpdate.Merge(userWorker);
                }
                else
                {
                    // No match found. This is a new worker the user wants to add.
                    newWorkersToPlace.Add(userWorker);
                }
            }

            // --- Step 4: Place the new workers into the array ---
            InsertNewWorkers(baseWorkers, newWorkersToPlace);
        }

        /// <summary>
        /// Finds a worker in the base list that matches a worker from the user's override file.
        /// </summary>
        private static JObject FindMatchingWorker(JArray baseWorkers, JObject userWorker)
        {
            var userId = userWorker["Id"]?.ToString();
            userWorker.Remove("Id");
            var userWorkerAudioExtractionId = userWorker["AudioExtractionId"]?.ToString() ?? userId;
            var userWorkerTranscriptionId = userWorker["TranscriptionId"]?.ToString() ?? userId;
            var userWorkerTranslationId = userWorker["TranslationId"]?.ToString() ?? userId;
            var userWorkerOutputId = userWorker["OutputId"]?.ToString() ?? userId;

            foreach (var baseWorkerToken in baseWorkers)
            {
                if (!(baseWorkerToken is JObject baseWorker)) continue;

                var baseWorkerAudioExtractionId = baseWorker["AudioExtractionId"]?.ToString();
                var baseWorkerTranscriptionId = baseWorker["TranscriptionId"]?.ToString();
                var baseWorkerTranslationId = baseWorker["TranslationId"]?.ToString();
                var baseWorkerOutputId = baseWorker["OutputId"]?.ToString();

                if (userWorkerAudioExtractionId != null
                    && userWorkerAudioExtractionId == baseWorkerAudioExtractionId)
                {
                    return baseWorker;
                }
                if (userWorkerTranscriptionId != null
                    && userWorkerTranscriptionId == baseWorkerTranscriptionId)
                {
                    return baseWorker;
                }
                if (userWorkerTranslationId != null
                    && userWorkerTranslationId == baseWorkerTranslationId)
                {
                    return baseWorker;
                }
                if (userWorkerOutputId != null
                    && userWorkerOutputId == baseWorkerOutputId)
                {
                    return baseWorker;
                }
            }

            return null; // No match found
        }

        /// <summary>
        /// Inserts new workers into the base worker list according to the '$insertAt' meta-property.
        /// This method processes insertions from highest index to lowest to ensure that insertions
        /// do not affect the position of subsequent insertion targets.
        /// </summary>
        private static void InsertNewWorkers(JArray baseWorkers, List<JObject> newWorkers)
        {
            // --- Step 1: Group new workers by their target index and separate those to be appended ---
            var insertionsByOriginalIndex = new Dictionary<int, List<JObject>>();
            var workersToAppend = new List<JObject>();

            foreach (var newWorker in newWorkers)
            {
                // Remove the meta-property so it doesn't pollute the final config.
                var insertAtToken = newWorker.GetValue("$insertAt", StringComparison.OrdinalIgnoreCase);
                newWorker.Remove("$insertAt");

                if (insertAtToken != null && int.TryParse(insertAtToken.ToString(), out int index))
                {
                    if (!insertionsByOriginalIndex.ContainsKey(index))
                    {
                        insertionsByOriginalIndex[index] = new List<JObject>();
                    }
                    // The order of workers in the user's file for the same index is preserved.
                    insertionsByOriginalIndex[index].Add(newWorker);
                }
                else
                {
                    // If '$insertAt' is missing or invalid, queue it for appending at the end.
                    workersToAppend.Add(newWorker);
                }
            }

            // --- Step 2: Process the grouped insertions from HIGHEST index to LOWEST ---
            // This is the crucial step that solves the shifting index problem.
            var sortedIndices = insertionsByOriginalIndex.Keys.OrderByDescending(k => k);

            foreach (var index in sortedIndices)
            {
                // Clamp the target index to be a valid insertion point (0 to Count).
                int insertionPoint = index;
                if (insertionPoint < 0) insertionPoint = 0;
                if (insertionPoint > baseWorkers.Count) insertionPoint = baseWorkers.Count;

                // Get the list of workers to insert at this original index.
                var workersForThisIndex = insertionsByOriginalIndex[index];

                // We insert them in reverse order so they end up in the correct final order.
                // E.g., to insert [A, B] at index 3, we first insert B at 3, then A at 3.
                // The final result is [..., item_2, A, B, item_3, ...].
                for (int i = workersForThisIndex.Count - 1; i >= 0; i--)
                {
                    baseWorkers.Insert(insertionPoint, workersForThisIndex[i]);
                }
            }

            // --- Step 3: Append any remaining workers ---
            foreach (var worker in workersToAppend)
            {
                baseWorkers.Add(worker);
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
            return ReplaceLongStringFromHybridToJson(
                originalText.Replace("^\\s*//.*$\n", string.Empty));
        }

        public static string ReplaceLongStringFromJsonToHybrid(string originalJson)
        {
            return Regex.Replace(
                originalJson,
                @"=_{3,}(?<text>.*?)_{3,}=",
                match => "\n" + STARTLONGSTRING + "\n\n" + match.Groups["text"].Value.Trim().Replace(@"\n", "\n") + "\n\n" + ENDLONGSTRING);
        }

        private static string ReplaceLongStringFromHybridToJson(string originalText)
        {
            return Regex.Replace(
                originalText,
                @"\n*=_{3,}(?<text>.*?)_{3,}=\n*",
                match => match.Groups["text"].Value.Trim().Replace("\n", @"\n").Trim(),
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