using FunscriptToolbox.Core;
using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.Properties;
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
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerbs
{
    public class SubtitleGeneratorConfig
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
        static SubtitleGeneratorConfig()
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

        public static SubtitleGeneratorConfig FromFile(string filepath, string userOverrideFilepath = null)
        {
            static T HandleJsonException<T>(Func<T> action, string path, string content)
            {
                try
                {
                    return action();
                }
                catch (JsonSerializationException ex)
                {
                    var adjustedFileName = path + ".as.json";
                    File.WriteAllText(adjustedFileName, "ERROR:\n" + ex.Message + "\n\n--- JSON CONTENT (remove this line and above line to get accurate line number)---\n" + content);
                    // It's better to throw a more specific exception or preserve the original stack trace.
                    throw new Exception($"Error parsing configuration file. A detailed report was written to '{adjustedFileName}'. Please correct the error in the original '.config' file and then delete the '.as.json' file.\n\nDetails: {ex.Message}", ex);
                }
            }

            static JObject ReadHybridJsonFile(string path, string content = null)
            {
                var adjustedContent = content ?? PreprocessHybridJsonFile(File.ReadAllText(path));
                return HandleJsonException(() => JObject.Parse(adjustedContent), path, content);
            }

            var config = ReadHybridJsonFile(filepath);

            if (userOverrideFilepath != null)
            {
                if (File.Exists(userOverrideFilepath))
                {
                    JObject userConfig = ReadHybridJsonFile(userOverrideFilepath);
                    MergeConfigs(config, userConfig);
                }
                else
                {
                    File.WriteAllText(userOverrideFilepath, Resources.Example_wipconfig);
                }
            }

            return HandleJsonException(
                () => ReadHybridJsonFile(filepath, config.ToString()).ToObject<SubtitleGeneratorConfig>(rs_serializer), 
                filepath, 
                config.ToString());
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
            // We clone the user config and remove 'Workers' so the default merge
            // doesn't corrupt our array with its index-based logic.
            var userConfigWithoutWorkers = (JObject)userConfig.DeepClone();
            userConfigWithoutWorkers.Remove("Workers");
            baseConfig.Merge(userConfigWithoutWorkers, new JsonMergeSettings
            {
                PropertyNameComparison = StringComparison.OrdinalIgnoreCase
            });

            // --- Step 2: Custom, ID-based merge for the 'Workers' array ---
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

            // --- Step 3: Place the new workers into the array ---
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

        public SubtitleGeneratorConfig()
        {
        }

        [JsonProperty(Order = 1)]
        public Language SourceLanguage { get; set; } = Language.FromString("ja");

        [JsonProperty(Order = 2)]
        public object[] SharedObjects { get; set; }

        [JsonProperty(Order = 3)]
        public SubtitleWorker[] Workers { get; set; }

        const string STARTLONGSTRING = "=_______________________________________";
        const string ENDLONGSTRING   = "_______________________________________=";

        private static string CreateLongString(string text)
        {
            return STARTLONGSTRING + text + ENDLONGSTRING;
        }

        private static string PreprocessHybridJsonFile(string originalText)
        { 
            return ReplaceLongStringFromHybridToJson(
                originalText.Replace("^\\s*//.*$\n", string.Empty));
        }

        private static string ReplaceLongStringFromJsonToHybrid(string originalJson)
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

        public static string GetExample()
        {
            var jtokenIdOverrides = new List<JTokenIdOverride>();
            var sharedObjects = new List<object>();

            var transcriberToolPurfviewWhisper = new TranscriberToolAudioPurfviewWhisper
            {
                ApplicationFullPath = @"[TOREPLACE-WITH-PathToPurfview]\Purfview-Whisper-Faster\faster-whisper-xxl.exe",
                Model = "Large-V2",
                ForceSplitOnComma = false
            };
            jtokenIdOverrides.Add(new JTokenIdOverride(transcriberToolPurfviewWhisper.GetType().Name, "TranscriberToolPurfviewWhisper"));
            sharedObjects.Add(transcriberToolPurfviewWhisper);

            var aiEngineGPT5ViaPoe = new AIEngineAPI()
            {
                BaseAddress = "https://api.poe.com/v1",
                Model = "GPT-5",
                APIKeyName = "APIKeyPoe"
            };
            jtokenIdOverrides.Add(new JTokenIdOverride(aiEngineGPT5ViaPoe.GetType().Name, "AIEngineGPT5ViaPoe"));
            sharedObjects.Add(aiEngineGPT5ViaPoe);

            var aiEngineGPT5 = new AIEngineAPI()
            {
                BaseAddress = "https://api.openai.com/v1",
                Model = "gpt-5",
                APIKeyName = "APIKeyOpenAI",
                RequestBodyExtension = Expando(
                    ("service_tier", "flex")),
                UseStreaming = false
            };
            jtokenIdOverrides.Add(new JTokenIdOverride(aiEngineGPT5.GetType().Name, "AIEngineGPT5"));
            sharedObjects.Add(aiEngineGPT5);

            var aiEngineGemini = new AIEngineAPI()
            {
                BaseAddress = "https://generativelanguage.googleapis.com/v1beta/openai/",
                Model = "gemini-2.5-pro",
                APIKeyName = "APIKeyGemini",
                RequestBodyExtension = Expando(
                    ("max_tokens", 64 * 1024),
                    ("extra_body", new
                    {
                        google = new
                        {
                            thinking_config = new
                            {
                                include_thoughts = true
                            }
                        }
                    }))
            };
            jtokenIdOverrides.Add(new JTokenIdOverride(aiEngineGemini.GetType().Name, "AIEngineGemini"));
            sharedObjects.Add(aiEngineGemini);

            var aiEngineLocalAPI = new AIEngineAPI
            {
                BaseAddress = "http://localhost:10000/v1",
                Model = "mistralai/mistral-small-3.2",
                ValidateModelNameInResponse = true,
                UseStreaming = true
            };
            jtokenIdOverrides.Add(new JTokenIdOverride(aiEngineLocalAPI.GetType().Name, "AIEngineLocalAPI"));
            sharedObjects.Add(aiEngineLocalAPI);

            AIPrompt AddPromptToSharedObjects(string name, string value)
            {
                var prompt = new AIPrompt(CreateLongString(value));
                jtokenIdOverrides.Add(new JTokenIdOverride(prompt.GetType().Name, name));
                sharedObjects.Add(prompt);
                return prompt;
            }

            var transcriberAudioFullSystemPrompt = AddPromptToSharedObjects("TranscriberAudioFullSystemPrompt", Resources.TranscriberAudioFullSystemPrompt);
            var transcriberAudioFullUserPrompt = AddPromptToSharedObjects("TranscriberAudioFullUserPrompt", Resources.TranscriberAudioFullUserPrompt);

            var transcriberAudioSingleVADSystemPrompt = AddPromptToSharedObjects("TranscriberAudioSingleVADSystemPrompt", Resources.TranscriberAudioSingleVADSystemPrompt);
            var transcriberAudioSingleVADUserPrompt = AddPromptToSharedObjects("TranscriberAudioSingleVADUserPrompt", Resources.TranscriberAudioSingleVADUserPrompt);

            var transcriberOnScreenTextSystemPrompt = AddPromptToSharedObjects("TranscriberOnScreenTextSystemPrompt", Resources.TranscriberOnScreenTextSystemPrompt);

            var transcriberVisualAnalystSystemPrompt = AddPromptToSharedObjects("TranscriberVisualAnalystSystemPrompt", Resources.TranscriberVisualAnalystSystemPrompt);
            var transcriberVisualAnalystUserPrompt = AddPromptToSharedObjects("TranscriberVisualAnalystUserPrompt", Resources.TranscriberVisualAnalystUserPrompt);

            var arbitrerSystemPrompt = AddPromptToSharedObjects("ArbitrerSystemPrompt", Resources.ArbitrerSystemPrompt);

            var translatorSystemPrompt = AddPromptToSharedObjects("TranslatorSystemPrompt", Resources.TranslatorSystemPrompt);
            var translatorNaturalistUserPrompt = AddPromptToSharedObjects("TranslatorNaturalistUserPrompt", Resources.TranslatorNaturalistUserPrompt);
            var translatorMaverickUserPrompt = AddPromptToSharedObjects("TranslatorMaverickUserPrompt", Resources.TranslatorMaverickUserPrompt);

            var config = new SubtitleGeneratorConfig()
            {
                SharedObjects = sharedObjects.ToArray(),
                Workers = new SubtitleWorker[]
                {
                    //---------------------------------------------------
                    // Audio extraction
                    new AudioExtractorFromVideo()
                    {
                        AudioExtractionId = "audio"
                    },
                    new AudioExtractorFromPcm()
                    {
                        AudioExtractionId = "audio-clean-waveform",
                        SourceAudioId = "audio",
                        SaveAsFileSuffixe = ".wav",
                        FfmpegParameters = "-af \"highpass=f=300,lowpass=f=3500,loudnorm=I=-16:TP=-1\"" // <lowpass>,anlmdn,agate=threshold=0.04,<loudnorm>  
                    },

                    //---------------------------------------------------
                    // Full transcription (whisper or AI)
                    new TranscriberAudioFull()
                    {
                        TranscriptionId = "full-whisper",
                        SourceAudioId = "audio",
                        MetadataProduced = "VoiceText",
                        TranscriberTool = transcriberToolPurfviewWhisper,
                    },
                    new TranscriberAudioFullAI()
                    {
                        TranscriptionId = "full-ai",
                        SourceAudioId = "audio",
                        Engine = aiEngineGemini,
                        SystemPrompt = transcriberAudioFullSystemPrompt,
                        UserPrompt = transcriberAudioFullUserPrompt,
                        MetadataProduced = "VoiceText",
                        MaxChunkDuration = TimeSpan.FromMinutes(5)
                    },
                    new TranscriberClone()
                    {
                        TranscriptionId = "full",
                        SourceId = "NEED-TO-BE-OVERRIDED" // Should be full-whisper or full-ai
                    },
                    new TranslatorGoogleV1API()
                    {
                        TranslationId = "full_google",
                        TranscriptionId = "full",
                        TargetLanguage = Language.FromString("en"),
                        MetadataNeeded = "VoiceText",
                        MetadataProduced = "TranslatedText"
                    },
                    new TranslatorAI()
                    {
                        TranslationId = "full_local-api",
                        TargetLanguage = Language.FromString("en"),
                        Engine = aiEngineLocalAPI,
                        Metadatas = new MetadataAggregator
                        {
                            TimingsSource = "full",
                            Sources = "full"
                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = translatorSystemPrompt,
                            MetadataNeeded = "VoiceText|OnScreenText",
                            MetadataAlwaysProduced = "TranslatedText",

                            BatchSize = 30,
                            NbItemsMinimumReceivedToContinue = 10
                        }
                    },
                    new SubtitleOutputSimpleSrt()
                    {
                        OutputId = "preliminary-srt",
                        FileSuffix = ".srt",
                        WorkerId = "full_google"
                    },

                    //---------------------------------------------------
                    // finalizing timings, manual-input and voice-texts clone
                    new SubtitleOutputSimpleSrt()
                    {
                        OutputId = "generated-manual-input-srt",
                        FileSuffix = ".manual-input.srt",
                        WorkerId = "full",
                        AddToFirstSubtitle = "[USER-REVISION-NEEDED] <= remove this line when revision is done\n{OngoingContext:The scene take place in...}\n{OngoingSpeakers:Woman}\nOther metadatas to use in the file:\n- GrabOnScreenText\n- VisualTraining (for visual-analyst)"
                    },
                    new TranscriberImportMetadatas()
                    {
                        TranscriptionId = "manual-input",
                        FileSuffix = ".manual-input.srt",
                        CanBeUpdated = true,
                        ProcessOnlyWhenStringIsRemoved = "[USER-REVISION-NEEDED]",
                    },
                    new TranscriberClone()
                    {
                        TranscriptionId = "timings",
                        SourceId = "manual-input"
                    },
                    new TranscriberAudioSingleVADAI()
                    {
                        TranscriptionId = "singlevad-ai",
                        SourceAudioId = "audio",
                        Engine = aiEngineGemini,
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "timings",
                            Sources = "onscreentext,manual-input"
                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = transcriberAudioSingleVADSystemPrompt,
                            UserPrompt = transcriberAudioSingleVADUserPrompt,
                            MetadataNeeded = "!NoVoice,!OnScreenText,!GrabOnScreenText",
                            MetadataAlwaysProduced = "VoiceText",
                            BatchSize = 150,
                            BatchSplitWindows = 5
                        }
                    },
                    new TranscriberClone()
                    {
                        TranscriptionId = "voice-texts",
                        SourceId = "NEED-TO-BE-OVERRIDED" // Should be full-ai or singlevad-ai
                    },

                    //---------------------------------------------------
                    // Use timings, manual-input and voice-texts to do all the rest of the tasks
                    new TranscriberAudioMergedVAD()
                    {
                        TranscriptionId = "mergedvad",
                        SourceAudioId = "audio",
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "timings",
                            Sources = "manual-input"
                        },
                        MetadataProduced = "VoiceText",
                        TranscriberTool = transcriberToolPurfviewWhisper
                    },
                    new TranscriberInteractifSetSpeaker()
                    {
                        TranscriptionId = "speakers",
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "timings",
                            Sources = "voice-texts, manual-input"
                        },
                        MetadataNeeded = "VoiceText,!GrabOnScreenText,!OnScreenText",
                        MetadataProduced = "Speaker",
                        MetadataPotentialSpeakers = "OngoingSpeakers"
                    },
                    new TranscriberImageAI()
                    {
                        TranscriptionId = "on-screen-texts",
                        FfmpegFilter = "crop=iw/2:ih:0:0",
                        Engine = aiEngineGemini,
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "timings",
                            Sources = "manual-input"
                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = transcriberOnScreenTextSystemPrompt,
                            MetadataNeeded = "GrabOnScreenText",
                            MetadataAlwaysProduced = "OnScreenText"
                        }
                    },
                    new TranscriberImageAI()
                    {
                        TranscriptionId = "visual-analysis",
                        FfmpegFilter = "v360=input=he:in_stereo=sbs:pitch=-35:v_fov=90:h_fov=90:d_fov=180:output=sg:w=1024:h=1024,drawtext=fontfile='C\\:/Windows/Fonts/Arial.ttf':text='[STARTTIME]':fontsize=10:fontcolor=white:x=10:y=10:box=1:boxcolor=black:boxborderw=5",
                        Engine = aiEngineGPT5ViaPoe,
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "timings",
                            Sources = "on-screen-texts,voice-texts,speakers,manual-input"
                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = transcriberVisualAnalystSystemPrompt,
                            UserPrompt = transcriberVisualAnalystUserPrompt,
                            TextAfterAnalysis = " --reasoning_effort medium",
                            MetadataNeeded = "!OnScreenText,!GrabOnScreenText",
                            MetadataAlwaysProduced = "ParticipantsPoses",
                            MetadataForTraining = "VisualTraining",

                            BatchSize = 30,
                            NbContextItems = 5,
                            NbItemsMinimumReceivedToContinue = 10
                        }
                    },
                    new SubtitleOutputComplexSrt()
                    {
                        OutputId = "metadatas-srt",
                        FileSuffix = ".metadatas.srt",
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "timings",
                            Sources = "voice-texts,on-screen-texts,visual-analysis,speakers,manual-input",
                        },
                        WaitForFinished = true
                    },
                    new TranscriberResetImportedMetadatas()
                    {
                        TranscriptionId = "metadatas-review",
                        SourceFileSuffix = ".metadatas.srt",
                        TranscriptionIdToBeUpdated = "manual-input",
                        AddToFirstSubtitle = "[USER-REVISION-NEEDED] <= remove this line when revision is done"
                    },

                    //---------------------------------------------------
                    // Use timings, manual-input, voice-texts and AI-generated metadatas to the translations and final arbitration
                    new TranslatorAI()
                    {
                        TranslationId = "translated-texts_maverick",
                        TargetLanguage = Language.FromString("en"),
                        Engine = aiEngineGPT5ViaPoe,
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "timings",
                            Sources = "on-screen-text,voice-texts,speakers,visual-analysis,manual-input"
                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = translatorSystemPrompt,
                            UserPrompt = translatorMaverickUserPrompt,
                            TextAfterAnalysis = " --reasoning_effort medium",

                            MetadataNeeded = "VoiceText|OnScreenText",
                            MetadataAlwaysProduced = "TranslatedText",
                            BatchSize = 300,
                            BatchSplitWindows = 10
                        }
                    },
                    new TranslatorAI()
                    {
                        TranslationId = "translated-texts_naturalist",
                        TargetLanguage = Language.FromString("en"),
                        Engine = aiEngineGPT5ViaPoe,
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "timings",
                            Sources = "on-screen-text,voice-texts,speakers,visual-analysis,manual-input"
                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = translatorSystemPrompt,
                            UserPrompt = translatorNaturalistUserPrompt,
                            TextAfterAnalysis = " --reasoning_effort medium",

                            MetadataNeeded = "VoiceText|OnScreenText",
                            MetadataAlwaysProduced = "TranslatedText",
                            BatchSize = 300,
                            BatchSplitWindows = 10
                        }
                    },
                    new TranscriberAggregator
                    {
                        TranscriptionId = "arbitrer-choices",
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "timings"
                        },
                        CandidatesSources = "translated-texts_maverick,translated-texts_naturalist,voice-texts,on-screen-text,mergedvad,full",
                        MetadataProduced = "CandidatesText",

                        WaitForFinished = true,
                        IncludeExtraItems = false
                    },
                    new TranslatorAI()
                    {
                        TranslationId = "arbitrer-final-choice",
                        TargetLanguage = Language.FromString("en"),
                        Engine = aiEngineGPT5ViaPoe,
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "timings",
                            Sources = "arbitrer-choices,voice-texts,on-screen-text,visual-analysis,speakers,manual-input"
                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = arbitrerSystemPrompt,
                            TextAfterAnalysis = " --reasoning_effort medium",

                            MetadataNeeded = "CandidatesText",
                            MetadataAlwaysProduced = "FinalText",
                            NbContextItems = 100,
                            BatchSize = 150,
                            BatchSplitWindows = 5
                        },
                        AutoMergeOn = "[!MERGED]",
                        AutoDeleteOn = "[!UNNEEDED]",
                        ExportMetadataSrt = true
                    },
                    new SubtitleOutputSimpleSrt()
                    {
                        OutputId = "arbitrer-final-choice-srt",
                        WorkerId = "arbitrer-final-choice",
                        FileSuffix = ".arbitrer-final-choice.srt",
                        SubtitlesToInject = CreateSubtitlesToInject(),
                    },
                    new SubtitleOutputCostReport()
                    {
                        OutputId = "costs",
                        FileSuffix = ".cost.txt",
                        OutputToConsole = false,
                        CanBeUpdated = true
                    },

                    //---------------------------------------------------
                    // Potential 'Learning' workflow
                    new TranscriberImportText
                    {
                        TranscriptionId = "final-user-edited",
                        FileSuffix = ".final.srt",
                        MetadataProduced = "FinalText"
                    },
                    new SubtitleOutputComplexSrt()
                    {
                        OutputId = "learning-srt",
                        FileSuffix = ".learning.srt",
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "arbitrer-final-choice",
                            Sources = "voice-texts,on-screen-texts,visual-analysis,speakers,manual-input",
                        },
                        TextSources = "final-user-edited,arbitrer-final-choice",
                        SkipWhenTextSourcesAreIdentical = "final-user-edited,arbitrer-final-choice"
                    }
                }
            };

            return ReplaceLongStringFromJsonToHybrid(
                OverridesIdInJObject(JObject.FromObject(config, rs_serializer), jtokenIdOverrides)
                .ToString());
        }

        private static SubtitleToInject[] CreateSubtitlesToInject()
        {
            return new[] {
                new SubtitleToInject()
                {
                    Origin = SubtitleToInjectOrigin.Start,
                    OffsetTime = TimeSpan.FromSeconds(0),
                    Duration = TimeSpan.FromSeconds(5),
                    Lines = new []
                    {
                        "Created by ???, using the FunscriptToolbox and SubtitleEdit."
                    }
                },
                new SubtitleToInject()
                {
                    Origin = SubtitleToInjectOrigin.Start,
                    OffsetTime = TimeSpan.FromSeconds(5),
                    Duration = TimeSpan.FromSeconds(2.5),
                    Lines = new []
                    {
                        "The initial transcription was generated with Gemini-2.5-pro,",
                        "and the translation was provided by the multiple AI models.",
                    }
                },
                new SubtitleToInject()
                {
                    Origin = SubtitleToInjectOrigin.Start,
                    OffsetTime = TimeSpan.FromSeconds(7.5),
                    Duration = TimeSpan.FromSeconds(2.5),
                    Lines = new []
                    {
                        "Both the transcription and translation underwent manual review",
                        "and adjustment to improve their accuracy and quality."
                    }
                }
            };
        }

        static ExpandoObject Expando(params (string key, object value)[] items)
        {
            var e = new ExpandoObject();
            var dict = (IDictionary<string, object>)e;
            foreach (var (k, v) in items) dict[k] = v!;
            return e;
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

        private class JTokenIdOverride
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

        static private JToken OverridesIdInJObject(JToken token, List<JTokenIdOverride> replacements)
            => OverridesIdInJObject(token, replacements, "$", null);

        static private JToken OverridesIdInJObject(
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