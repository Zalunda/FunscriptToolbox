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
        const string STARTLONGSTRING = "__=";
        const string ENDLONGSTRING = "=__";

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

        public static SubtitleGeneratorConfig FromFile(string filepath)
        {
            try
            {
                var content = ReplaceLongStringFromHybridToJson(File.ReadAllText(filepath));
                using var reader = new StringReader(content);
                using var jsonReader = new JsonTextReader(reader);
                rs_serializer.ReferenceResolver = new ValidatingReferenceResolver(rs_serializer.ReferenceResolver);
                return rs_serializer.Deserialize<SubtitleGeneratorConfig>(jsonReader);
            }
            catch (Exception ex)
            {
                throw new Exception($"Error while parsing file '{filepath}': {ex.Message}", ex);
            }
        }

        public SubtitleGeneratorConfig()
        {
        }

        [JsonProperty(Order = 1)]
        public object[] SharedObjects { get; set; }

        [JsonProperty(Order = 2)]
        public SubtitleWorker[] Workers { get; set; }

        private static string CreateLongString(string text)
        {
            return STARTLONGSTRING + text + ENDLONGSTRING;
        }

        private static string ReplaceLongStringFromJsonToHybrid(string originalJson)
        {
            return Regex.Replace(
                originalJson,
                @"__=(?<text>.*?)=__",
                match => STARTLONGSTRING + "\n" + match.Groups["text"].Value.Replace(@"\n", "\n") + ENDLONGSTRING);
        }

        private static string ReplaceLongStringFromHybridToJson(string originalText)
        {
            return Regex.Replace(
                originalText,
                @"(__=(?<text>.*?)=__)",
                match => match.Groups["text"].Value.Replace("\n", @"\n").Trim(),
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
            var userPromptArbitrer = AddPromptToSharedObjects("UserPromptArbitrer", Resources.UserPromptArbitrer);

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
                        TranscriptionId = "full",
                        SourceAudioId = "audio",
                        MetadataProduced = "VoiceText",
                        TranscriberTool = transcriberToolPurfviewWhisper,
                    },
                    new TranslatorGoogleV1API()
                    {
                        TranscriptionId = "full",
                        TranslationId = "google",
                        TargetLanguage = Language.FromString("en"),
                        MetadataNeeded = "VoiceText",
                        MetadataProduced = "TranslatedText"
                    },
                    new TranslatorAI()
                    {
                        TranscriptionId = "full",
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
                    //new SubtitleOutputSrt()
                    //{
                    //    FileSuffix = ".perfect-vad-potential.srt",
                    //    Metadatas = new MetadataAggregator()
                    //    {
                    //        TimingsSource = "full",
                    //    }
                    //    WorkerId = "full",

                    //}
                    new SubtitleOutputSingleTranslationSrt()
                    {
                        FileSuffix = ".perfect-vad-potential.srt",
                        WorkerId = "full_local-api",
                        AddToFirstSubtitle = "{OngoingContext:The scene take place in...}\n{OngoingSpeakers:Woman}\nOther metadatas to use in the file:\n- GrabOnScreenText\n- VisualTraining (for visual-analyst)\n- Action (if not using visual-analyst)"
                    },

                    new TranscriberPerfectVAD()
                    {
                        TranscriptionId = "perfectvad",
                        FileSuffix = ".perfect-vad.srt"
                    },
                    new TranscriberAudioMergedVAD()
                    {
                        TranscriptionId = "mergedvad",
                        SourceAudioId = "audio",
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "perfectvad",
                            Sources = new [] { "perfectvad" }
                        },
                        MetadataProduced = "VoiceText",
                        TranscriberTool = transcriberToolPurfviewWhisper
                    },
                    new TranscriberImageAI()
                    {
                        TranscriptionId = "onscreen",
                        FfmpegFilter = "crop=iw/2:ih:0:0",
                        Engine = new AIEngineAPI()
                        {
                            BaseAddress = "https://generativelanguage.googleapis.com/v1beta/openai/",
                            Model = "gemini-2.5-pro",
                            APIKeyName = "APIGeminiAI"
                        },
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "perfectvad",
                            Sources = new [] { "perfectvad" }
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
                            TimingsSource = "perfectvad",
                            Sources = new [] { "onscreen", "perfectvad" }
                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = systemPromptTranscriberAudioSingleVAD,
                            MetadataNeeded = "!NoVoice,!OnScreenText,!GrabOnScreenText",
                            MetadataAlwaysProduced = "VoiceText"
                        }
                    },
                    new TranscriberInteractifSetSpeaker()
                    {
                        TranscriptionId = "validated-speakers",
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "perfectvad",
                            Sources = new [] { "perfectvad", "singlevad" },
                        },
                        MetadataNeeded = "VoiceText",
                        MetadataProduced = "Speaker",
                        MetadataPotentialSpeakers = "OngoingSpeakers",
                        MetadataDetectedSpeaker = "Speaker-A"
                    },
                    new TranscriberImageAI()
                    {
                        Enabled = false,

                        TranscriptionId = "visual-analyst",
                        FfmpegFilter = "v360=input=he:in_stereo=sbs:pitch=-35:v_fov=90:h_fov=90:d_fov=180:output=sg:w=1024:h=1024",
                        KeepTemporaryFiles = true,
                        Engine = new AIEngineAPI()
                        {
                            BaseAddress = "https://api.poe.com/v1",
                            Model = "gpt5",
                            APIKeyName = "APIKeyPoe",
                        },
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "perfectvad",
                            Sources = new [] { "onscreen", "validated-speakers", "singlevad", "perfectvad" }
                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = systemPromptTranscriberVisualAnalyst,
                            MetadataNeeded = "!OnScreenText,!GrabOnScreenText",
                            MetadataAlwaysProduced = "ParticipantDynamics",
                            MetadataForTraining = "VisualTraining",

                            BatchSize = 5,
                            NbContextItems = 0,
                            NbItemsMinimumReceivedToContinue = 5,
                            TextAfterTrainingData = "Only use those images to identify the characters and to understand how the man's hand can be seen in a POV-view like this. Do not use part of those image for your analysis of nodes.",
                            TextBeforeAnalysis = "Begin Node Analysis:",
                            TextAfterAnalysis = "**REMINDER**:\n" +
                            //"For EVERY image, not just the first one: Ask yourself, where are POV-man's hands, are they grabbing breasts? and they in a girl's short?\n" +
                            //"Do not stop analysing man's limb position even if they hadn't be seen for a while (for example, where is POV-man's left hand?). Everything need to be reevaluated on every image.\n" +
                            "Don't forget to return a node for every nodes received. Count the number of nodes in the Begin Analysis section and the number of nodes in your answer and make sure that it match! Do not return a single node!"
                            //"FOR DEBUGGING PURPOSE: After the producing the JSON, please tell me if you can find POV-man's hands on the image of node 3:27.213?\n" +
                            //"Last time I asked, you said: Yes — in the image for node 00:03:27.213 I can see the POV-man's right hand resting on the bed/near the right side of his torso (visible near Ena's hip), and his left hand is not clearly visible in the frame.  (sorry to work like this, I trying going through an API)\n" +
                            //"You can't see that his right hand is firmly on Ena's breast, and his left and are less clearly on Hana's breast?"
                        }
                    },
                    new TranslatorAI()
                    {
                        Enabled = false,

                        TranscriptionId = "singlevad",
                        TranslationId = "analyst",
                        TargetLanguage = Language.FromString("en"),
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
                                })),
                            UseStreaming = true
                        },
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "perfectvad",
                            Sources = new [] { "onscreen", "visual-analyst", "singlevad", "perfectvad" }
                            // TODO MERGE add all metadata in output
                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = systemPromptTranslator,
                            FirstUserPrompt = userPromptTranslatorAnalyst,
                            MetadataNeeded = "",
                            MetadataAlwaysProduced = "Analyzed"
                        }
                    },
                    // TODO REMOVE -------------------------------
                    new TranslatorAI()
                    {
                        TranscriptionId = "singlevad",
                        TranslationId = "local-api-1", // No Info
                        Enabled = true,
                        TargetLanguage = Language.FromString("en"),
                        Engine = new AIEngineAPI {
                            BaseAddress = "http://localhost:10000/v1",
                            Model = "mistralai/mistral-small-3.2",
                            ValidateModelNameInResponse = true
                        },
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "perfectvad",
                            Sources = new [] { "onscreen", "singlevad", "perfectvad"}
                        },
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
                    new TranslatorAI()
                    {
                        TranscriptionId = "singlevad",
                        TranslationId = "local-api-2", // Full Info
                        Enabled = true,
                        TargetLanguage = Language.FromString("en"),
                        Engine = new AIEngineAPI {
                            BaseAddress = "http://localhost:10000/v1",
                            Model = "mistralai/mistral-small-3.2",
                            ValidateModelNameInResponse = true
                        },
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "perfectvad",
                            Sources = new [] { "onscreen", "visual-analyst", "singlevad", "perfectvad" }
                        },
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
                    // TODO -------------------------------
                    new TranslatorAI()
                    {
                        Enabled = false,

                        TranscriptionId = "singlevad",
                        TranslationId = "naturalist",
                        TargetLanguage = Language.FromString("en"),
                        Engine = new AIEngineAPI()
                        {
                            BaseAddress = "https://api.poe.com/v1",
                            Model = "GPT-5", // Or "JAVTrans-GPT5" without systemPrompt below
                            APIKeyName = "APIKeyPoe"
                        },
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "perfectvad",
                            Sources = new [] { "onscreen", "visual-analyst", "analyst", "perfectvad" }
                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = systemPromptTranslator,
                            FirstUserPrompt = userPromptTranslatorNaturalist,

                            MetadataNeeded = "VoiceText|OnScreenText",
                            MetadataAlwaysProduced = "TranslatedText",
                        }
                    },
                    new TranslatorAI()
                    {
                        Enabled = false,

                        TranscriptionId = "singlevad",
                        TranslationId = "maverick",
                        TargetLanguage = Language.FromString("en"),
                        Engine = new AIEngineAPI()
                        {
                            BaseAddress = "https://api.poe.com/v1",
                            Model = "GPT-5", // Or "JAVTrans-GPT5" without systemPrompt below
                            APIKeyName = "APIKeyPoe"
                        },
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "perfectvad",
                            Sources = new [] { "onscreen", "visual-analyst", "analyst", "perfectvad" }
                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = systemPromptTranslator,
                            FirstUserPrompt = userPromptTranslatorMaverick,

                            MetadataNeeded = "VoiceText|OnScreenText",
                            MetadataAlwaysProduced = "TranslatedText",
                        }
                    },
                    new TranscriberAggregator
                    {
                        TranscriptionId = "candidates-digest",
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "perfectvad"
                        },
                        CandidatesSources = new [] { "singlevad_local-api-1", "singlevad_local-api-2", "onscreen", "singlevad", "mergedvad", "full" }, // "singlevad_maverick-GPT5", "singlevad_naturalist-GPT5"
                        MetadataProduced = "CandidatesText",

                        WaitForFinished = true,
                        IncludeExtraItems = false
                    },
                    new TranscriberAggregator
                    {
                        TranscriptionId = "partial-candidates-digest",
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "perfectvad"
                        },
                        CandidatesSources = new [] { "singlevad_local-api-1", "singlevad_local-api-2", "onscreen", "singlevad", "mergedvad", "full" }, // "singlevad_maverick-GPT5", "singlevad_naturalist-GPT5"
                        MetadataProduced = "CandidatesText",

                        WaitForFinished = false,
                        IncludeExtraItems = false
                    },
                    new TranslatorAI()
                    {
                        Enabled = false,

                        TranscriptionId = "candidates-digest",
                        TranslationId = "arbitrer",
                        TargetLanguage = Language.FromString("en"),
                        Engine = new AIEngineAPI()
                        {
                            BaseAddress = "https://api.poe.com/v1",
                            Model = "GPT-5", // Or "JAVTrans-Arbitrer" without systemPrompt below
                            APIKeyName = "APIKeyPoe"
                        },
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "perfectvad",
                            Sources = new [] { "onscreen", "visual-analyst", "singlevad", "perfectvad"}
                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = systemPromptArbitrer,
                            FirstUserPrompt = userPromptArbitrer,

                            MetadataNeeded = "CandidatesText",
                            MetadataAlwaysProduced = "FinalText",
                        }
                    },
                    new SubtitleOutputCostReport()
                    {
                        FileSuffix = ".cost.txt",
                        OutputToConsole = false
                    },

                    // TODO 
                    new SubtitleOutputSingleTranslationSrt()
                    {
                        WorkerId = "candidates-digest_arbitrer",
                        FileSuffix = ".final-arbitrer-choice.srt",
                        SubtitlesToInject = CreateSubtitlesToInject(),
                    },
                    new SubtitleOutputSingleTranslationSrt()
                    {
                        WorkerId = "partial-candidates-digest",
                        FileSuffix = ".partial-arbitrer-choice-for-debugging.srt",
                    },
                    new SubtitleOutputMultiTranslationSrt()
                    {
                        Enabled = false,
                        TranscriptionId = "mergedvad",
                        TranslationsOrder = new [] { "analyst", "naturalist-GPT5", "*" },
                        FileSuffix = ".mergedvad.srt"
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