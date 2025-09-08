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
            string userWorkerType = userWorker["$type"]?.ToString() ?? string.Empty;
            if (string.IsNullOrEmpty(userWorkerType)) return null; // Can't match without a type.

            var userWorkerTranscriptionId = userWorker["TranscriptionId"]?.ToString();
            var userWorkerTranslationId = userWorker["TranslationId"]?.ToString();
            var userWorkerAudioExtractionId = userWorker["AudioExtractionId"]?.ToString();
            var userWorkerOutputId = userWorker["OutputId"]?.ToString();

            foreach (var baseWorkerToken in baseWorkers)
            {
                if (!(baseWorkerToken is JObject baseWorker)) continue;

                string baseWorkerType = baseWorker["$type"]?.ToString() ?? string.Empty;
                if (baseWorkerType != userWorkerType) continue; // Types must match.

                var baseWorkerTranscriptionId = baseWorker["TranscriptionId"]?.ToString();
                var baseWorkerTranslationId = baseWorker["TranslationId"]?.ToString();
                var baseWorkerAudioExtractionId = baseWorker["AudioExtractionId"]?.ToString();
                var baseWorkerOutputId = baseWorker["OutputId"]?.ToString();

                // For Translation
                if (userWorkerTranslationId != null && userWorkerTranscriptionId != null
                    && userWorkerTranslationId == baseWorkerTranslationId 
                    && userWorkerTranscriptionId == baseWorkerTranscriptionId)
                {
                    return baseWorker;
                }

                // For Transcription
                if (userWorkerTranslationId == null && userWorkerTranscriptionId != null
                    && userWorkerTranscriptionId == baseWorkerTranscriptionId)
                {
                    return baseWorker;
                }

                if (userWorkerAudioExtractionId != null
                    && userWorkerAudioExtractionId == baseWorkerAudioExtractionId)
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

        public static string GetDefaultExample()
        {
            var jtokenIdOverrides = new List<JTokenIdOverride>();
            var sharedObjects = new List<object>();

            jtokenIdOverrides.Add(new JTokenIdOverride(typeof(TranscriberToolAudioPurfviewWhisper).Name, "TranscriberToolPurfviewWhisper"));
            var transcriberToolPurfviewWhisper = new TranscriberToolAudioPurfviewWhisper
            {
                ApplicationFullPath = @"[TOREPLACE-WITH-PathToPurfview]\Purfview-Whisper-Faster\faster-whisper-xxl.exe",
                Model = "Large-V2",
                ForceSplitOnComma = false
            };
            sharedObjects.Add(transcriberToolPurfviewWhisper);

            AIPrompt AddPromptToSharedObjects(string name, string value)
            {
                jtokenIdOverrides.Add(new JTokenIdOverride(typeof(AIPrompt).Name, name));
                var prompt = new AIPrompt(CreateLongString(value));
                sharedObjects.Add(prompt);
                return prompt;
            }

            var systemPromptTranscriberOnScreenText = AddPromptToSharedObjects("SystemPromptTranscriberOnScreenText", Resources.SystemPromptTranscriberOnScreenText);
            var systemPromptTranscriberVisualAnalyst = AddPromptToSharedObjects("SystemPromptTranscriberVisualAnalyst", Resources.SystemPromptTranscriberVisualAnalyst);
            var systemPromptTranscriberAudioSingleVAD = AddPromptToSharedObjects("SystemPromptTranscriberAudioSingleVAD", Resources.SystemPromptTranscriberAudioSingleVAD);
            var systemPromptTranscriberAudioFull = AddPromptToSharedObjects("SystemPromptTranscriberAudioFull", Resources.SystemPromptTranscriberAudioFull);
            var systemPromptArbitrer = AddPromptToSharedObjects("SystemPromptArbitrer", Resources.SystemPromptArbitrer);
            var userPromptTranslatorAnalyst = AddPromptToSharedObjects("UserPromptTranslatorAnalyst", Resources.UserPromptTranslatorAnalyst);
            var systemPromptTranslator = AddPromptToSharedObjects("SystemPromptTranslator", Resources.SystemPromptTranslator);
            var userPromptTranslatorNaturalist = AddPromptToSharedObjects("UserPromptTranslatorNaturalist", Resources.UserPromptTranslatorNaturalist);
            var userPromptTranslatorMaverick = AddPromptToSharedObjects("UserPromptTranslatorMaverick", Resources.UserPromptTranslatorMaverick);
            var userPromptVisualAnalyst = AddPromptToSharedObjects("UserPromptVisualAnalyst", Resources.UserPromptVisualAnalyst);

            var config = new SubtitleGeneratorConfig()
            {
                SharedObjects = sharedObjects.ToArray(),
                Workers = new SubtitleWorker[]
                {
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
                        Enabled = false,
                        Engine = new AIEngineAPI()
                        {
                            BaseAddress = "https://generativelanguage.googleapis.com/v1beta/openai/",
                            Model = "gemini-2.5-pro",
                            APIKeyName = "APIGeminiAI",
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
                        },
                        SystemPrompt = systemPromptTranscriberAudioFull,
                        MetadataProduced = "VoiceText",
                        MaxChunkDuration = TimeSpan.FromMinutes(5)
                    },
                    new TranslatorGoogleV1API()
                    {
                        TranscriptionId = "full-whisper",
                        TranslationId = "google",
                        TargetLanguage = Language.FromString("en"),
                        MetadataNeeded = "VoiceText",
                        MetadataProduced = "TranslatedText"
                    },
                    new TranslatorAI()
                    {
                        TranscriptionId = "full-whisper",
                        TranslationId = "local-api",
                        Enabled = false,
                        TargetLanguage = Language.FromString("en"),
                        Engine = new AIEngineAPI {
                            BaseAddress = "http://localhost:10000/v1",
                            Model = "mistralai/mistral-small-3.2",
                            ValidateModelNameInResponse = true,
                            UseStreaming = true
                        },
                        Metadatas = null,
                        Options = new AIOptions()
                        {
                            SystemPrompt = systemPromptTranslator,
                            MetadataNeeded = "VoiceText|OnScreenText",
                            MetadataAlwaysProduced = "TranslatedText",

                            BatchSize = 30,
                            NbContextItems = null,
                            NbItemsMinimumReceivedToContinue = 10
                        }
                    },
                    new SubtitleOutputSimpleSrt()
                    {
                        FileSuffix = ".perfect-vad-potential.srt",
                        WorkerId = "full-whisper_google",
                        AddToFirstSubtitle = "{OngoingContext:The scene take place in...}\n{OngoingSpeakers:Woman}\nOther metadatas to use in the file:\n- GrabOnScreenText\n- VisualTraining (for visual-analyst)\n- Action (if not using visual-analyst)"
                    },

                    new TranscriberPerfectVAD()
                    {
                        TranscriptionId = "perfect-vad",
                        FileSuffix = ".perfect-vad.srt"
                    },
                    new TranscriberAudioMergedVAD()
                    {
                        TranscriptionId = "mergedvad",
                        SourceAudioId = "audio",
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "perfect-vad",
                            Sources = "perfect-vad"
                        },
                        MetadataProduced = "VoiceText",
                        TranscriberTool = transcriberToolPurfviewWhisper
                    },
                    new TranscriberImageAI()
                    {
                        TranscriptionId = "onscreentext",
                        FfmpegFilter = "crop=iw/2:ih:0:0",
                        Engine = new AIEngineAPI()
                        {
                            BaseAddress = "https://generativelanguage.googleapis.com/v1beta/openai/",
                            Model = "gemini-2.5-pro",
                            APIKeyName = "APIGeminiAI"
                        },
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "perfect-vad",
                            Sources = "perfect-vad"
                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = systemPromptTranscriberOnScreenText,
                            MetadataNeeded = "GrabOnScreenText",
                            MetadataAlwaysProduced = "OnScreenText",

                            NbContextItems = null
                        }
                    },
                    new TranscriberAudioSingleVADAI()
                    {
                        TranscriptionId = "singlevad",
                        SourceAudioId = "audio",
                        Engine = new AIEngineAPI()
                        {
                            // https://ai.google.dev/gemini-api/docs/openai#rest_2

                            BaseAddress = "https://generativelanguage.googleapis.com/v1beta/openai/",
                            Model = "gemini-2.5-pro",
                            APIKeyName = "APIGeminiAI",
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
                        },
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "perfect-vad",
                            Sources = "onscreentext,perfect-vad"
                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = systemPromptTranscriberAudioSingleVAD,
                            MetadataNeeded = "!NoVoice,!OnScreenText,!GrabOnScreenText",
                            MetadataAlwaysProduced = "VoiceText",
                            BatchSize = 150
                        }
                    },
                    new TranscriberInteractifSetSpeaker()
                    {
                        TranscriptionId = "validated-speakers",
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "perfect-vad",
                            Sources = "perfect-vad,singlevad"
                        },
                        MetadataNeeded = "VoiceText",
                        MetadataProduced = "Speaker",
                        MetadataPotentialSpeakers = "OngoingSpeakers",
                        MetadataDetectedSpeaker = "Speaker-A",
                    },
                    new TranscriberImageAI()
                    {
                        TranscriptionId = "visual-analyst",
                        Enabled = false,
                        FfmpegFilter = "v360=input=he:in_stereo=sbs:pitch=-35:v_fov=90:h_fov=90:d_fov=180:output=sg:w=1024:h=1024",
                        Engine = new AIEngineAPI()
                        {
                            BaseAddress = "https://api.poe.com/v1",
                            Model = "gpt5",
                            APIKeyName = "APIKeyPoe",
                        },
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "perfect-vad",
                            Sources = "onscreentext,validated-speakers,singlevad,perfect-vad"
                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = systemPromptTranscriberVisualAnalyst,
                            UserPrompt = userPromptVisualAnalyst,
                            MetadataNeeded = "!OnScreenText,!GrabOnScreenText",
                            MetadataAlwaysProduced = "ParticipantsPoses",
                            MetadataForTraining = "VisualTraining",

                            BatchSize = 50, // 5 => ~100pts per image, 30 => ~35pts per image, 50 => ~32pts per image.
                            NbContextItems = 5,
                            NbItemsMinimumReceivedToContinue = 10
                        }
                    },
                    new TranslatorAI()
                    {
                        TranscriptionId = "singlevad",
                        TranslationId = "maverick",
                        TargetLanguage = Language.FromString("en"),
                        Engine = new AIEngineAPI()
                        {
                            BaseAddress = "https://api.poe.com/v1",
                            Model = "GPT-5",
                            APIKeyName = "APIKeyPoe"
                        },
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "perfect-vad",
                            Sources = "onscreentext,validated-speakers,visual-analyst,perfect-vad"
                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = systemPromptTranslator,
                            UserPrompt = userPromptTranslatorMaverick,

                            MetadataNeeded = "VoiceText|OnScreenText",
                            MetadataAlwaysProduced = "TranslatedText",
                            BatchSize = 300
                        }
                    },
                    new TranslatorAI()
                    {
                        TranscriptionId = "singlevad",
                        TranslationId = "naturalist",
                        TargetLanguage = Language.FromString("en"),
                        Engine = new AIEngineAPI()
                        {
                            BaseAddress = "https://api.poe.com/v1",
                            Model = "GPT-5",
                            APIKeyName = "APIKeyPoe"
                        },
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "perfect-vad",
                            Sources = "onscreentext,validated-speakers,visual-analyst,perfect-vad"
                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = systemPromptTranslator,
                            UserPrompt = userPromptTranslatorNaturalist,

                            MetadataNeeded = "VoiceText|OnScreenText",
                            MetadataAlwaysProduced = "TranslatedText",
                            BatchSize = 300
                        }
                    },
                    new TranscriberAggregator
                    {
                        TranscriptionId = "candidates-digest",
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "perfect-vad"
                        },
                        CandidatesSources = "singlevad_maverick,singlevad_naturalist,onscreentext,singlevad,mergedvad,full",
                        MetadataProduced = "CandidatesText",

                        WaitForFinished = true,
                        IncludeExtraItems = false
                    },
                    new TranscriberAggregator
                    {
                        TranscriptionId = "partial-candidates-digest",
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "perfect-vad"
                        },
                        CandidatesSources = "singlevad_maverick,singlevad_naturalist,onscreentext,singlevad,mergedvad,full",
                        MetadataProduced = "CandidatesText",

                        WaitForFinished = false,
                        IncludeExtraItems = false
                    },
                    new TranslatorAI()
                    {
                        TranscriptionId = "candidates-digest",
                        TranslationId = "arbitrer",
                        TargetLanguage = Language.FromString("en"),
                        Engine = new AIEngineAPI()
                        {
                            BaseAddress = "https://api.poe.com/v1",
                            Model = "GPT-5",
                            APIKeyName = "APIKeyPoe"
                        },
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "perfect-vad",
                            Sources = "onscreentext,visual-analyst,validated-speakers,perfect-vad"
                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = systemPromptArbitrer,

                            MetadataNeeded = "CandidatesText",
                            MetadataAlwaysProduced = "FinalText",
                            NbContextItems = 15,
                            BatchSize = 150
                        },
                        AutoMergeOn = "[!MERGED]",
                        AutoDeleteOn = "[!UNNEEDED]",
                        ExportMetadataSrt = true
                    },
                    new TranscriberImport
                    {
                        TranscriptionId = "final-user-edited",
                        FileSuffix = ".final.srt",
                        MetadataProduced = "FinalText"
                    },
                    new SubtitleOutputCostReport()
                    {
                        FileSuffix = ".cost.txt",
                        OutputToConsole = false
                    },
                    new SubtitleOutputComplexSrt()
                    {
                        FileSuffix = ".wip-metadatas.srt",
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "candidates-digest_arbitrer",
                            Sources = "perfect-vad,onscreentext,singlevad,validated-speakers,visual-analyst",
                        },
                    },
                    new SubtitleOutputSimpleSrt()
                    {
                        WorkerId = "candidates-digest_arbitrer",
                        FileSuffix = ".final-arbitrer-choice.srt",
                        SubtitlesToInject = CreateSubtitlesToInject(),
                    },
                    new SubtitleOutputComplexSrt()
                    {
                        FileSuffix = ".learning.srt",
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "candidates-digest_arbitrer",
                            Sources = "perfect-vad,onscreentext,validated-speakers", //"visual-analyst"
                        },
                        TextSources = "final-user-edited,candidates-digest_arbitrer,singlevad,mergedvad,full", // singlevad_maverick,singlevad_naturalist,singlevad,mergedvad,full"
                        SkipWhenTextSourcesAreIdentical = "final-user-edited,candidates-digest_arbitrer"
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