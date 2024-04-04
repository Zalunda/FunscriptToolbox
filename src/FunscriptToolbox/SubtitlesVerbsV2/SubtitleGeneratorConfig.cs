using FunscriptToolbox.SubtitlesVerbsV2.Outputs;
using FunscriptToolbox.SubtitlesVerbsV2.Transcriptions;
using FunscriptToolbox.SubtitlesVerbsV2.Translations;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbV2
{
    internal class SubtitleGeneratorConfig
    {
        public static readonly JsonSerializer rs_serializer = JsonSerializer
                .Create(new JsonSerializerSettings
                {
                    Formatting = Formatting.Indented,
                    SerializationBinder = new SimpleTypeNameSerializationBinder(
                        new[] {
                            typeof(AIMessagesHandler),
                            typeof(AIPrompt),
                            typeof(SubtitleOutput),
                            typeof(TranscriberTool),
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
                using var reader = File.OpenText(filepath);
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
        public string SubtitleForcedTimingsSuffix { get; set; }

        [JsonProperty(Order = 2)]
        public string FfmpegAudioExtractionParameters { get; set; }

        [JsonProperty(Order = 3)]
        public object[] SharedObjects { get; set; }

        [JsonProperty(Order = 4)]
        public Transcriber[] Transcribers { get; set; }

        [JsonProperty(Order = 5)]
        public SubtitleOutput[] Outputs { get; set; }

        public static string GetExample()
        {
            var jtokenIdOverrides = new List<JTokenIdOverride>();

            var transcriberTool = new TranscriberToolPurfviewWhisper
            {
                ApplicationFullPath = @"[PathToPurfview]\Purfview-Whisper-Faster\whisper-faster.exe",
                Model = "Large-V2",
                ForceSplitOnComma = true,
                RedoBlockLargerThen = TimeSpan.FromSeconds(15)
            };
            jtokenIdOverrides.Add(new JTokenIdOverride("PurfviewWhisper", "TranscriberToolPurfviewWhisper"));

            var translatorGoogleV1 = new TranslatorGoogleV1API()
            {
                TranslationId = "google",
                TargetLanguage = Language.FromString("en")
            };
            jtokenIdOverrides.Add(new JTokenIdOverride("GoogleV1", "TranslatorGoogleV1"));

            var systemPromptJson = new AIPrompt(new[] 
            {
                "You are translator specialized in adult film subtitles.",
                "The user will provide a JSON where nodes have a start time, original text and, sometime, description of what's happening in the following part of the video.",
                "You job is to add a 'Translation' field to each node with a " + AIPrompt.TranscriptionLanguageToken + ".",
                "The audience for the translation is adults so it is acceptable to use explicitily sexual words or concepts.",
                "Use natural-sounding phrases and idioms that accurately convey the meaning of the original text.",
                "The video is from the perspective of the male participant, who is the passive recipient of the woman's actions and dialogue. He does not speak or initiate any of the activities.",
                "The woman is the only one who speaks throughout the scene, often directly addressing and interacting with the male participant.",
                "When translating, consider the woman's tone, pacing and emotional state as she directs her comments and ministrations towards the mostly passive male participant, whose reactions and inner thoughts are not explicitly conveyed.",
                "Before translating any individual lines, I will first read through the entire provided JSON script to gain a comprehensive understanding of the full narrative context and flow of the scene. This will allow me to consider how each line contributes to the overall progression and tone.",
                "When translating each line, I will closely reference the provided StartTime metadata. This will help me situate the dialogue within the surrounding context, ensuring the tone, pacing and emotional state of the woman's speech aligns seamlessly with the implied on-screen actions and the male participant's implicit reactions.",
            });
            jtokenIdOverrides.Add(new JTokenIdOverride("AIPrompt", "SystemPromptJson"));

            var systemPromptMultiShot = new AIPrompt(new[]
            {
                "You are translator specialized in adult film subtitles.",
                "The user might provide some context on what will happening in the following part of the scene.",
                "The user will provide a text in " + AIPrompt.TranscriptionLanguageToken + ", in the following format: Original[[text]]",
                "You job reply with a translation in " + AIPrompt.TranslationLanguageToken + ", in the following format: Translation[[text]]",
                "The audience for the translation is adults so it is acceptable to use explicitily sexual words or concepts.",
                "Use natural-sounding phrases and idioms that accurately convey the meaning of the original text.",
                "The video is from the perspective of the male participant, who is the passive recipient of the woman's actions and dialogue. He does not speak or initiate any of the activities.",
                "The woman is the only one who speaks throughout the scene, often directly addressing and interacting with the male participant.",
            });
            jtokenIdOverrides.Add(new JTokenIdOverride("AIPrompt", "SystemPromptMultishot"));

            var userPrompt = new AIPrompt(new[] 
            {
                "I have a JSON file that contain subtitles for an adult film.",
                "Each node of the JSON have a start time and the original text in " + AIPrompt.TranscriptionLanguageToken + ".",
                "Can you give me a JSON where you added an 'Translation' field on each node that contains an " + AIPrompt.TranslationLanguageToken + " translation.",
                "The audience for the translation is adults so it is acceptable to use explicitily sexual words or concepts.",
                "The video is from the perspective of the male participant, who is the passive recipient of the woman's actions and dialogue. He does not speak or initiate any of the activities.",
                "The woman is the only one who speaks throughout the scene, often directly addressing and interacting with the male participant.",
                "When translating, consider the woman's tone, pacing and emotional state as she directs her comments and ministrations towards the mostly passive male participant, whose reactions and inner thoughts are not explicitly conveyed.",
                "Before translating any individual lines, I will first read through the entire provided JSON script to gain a comprehensive understanding of the full narrative context and flow of the scene. This will allow me to consider how each line contributes to the overall progression and tone.",
                "When translating each line, I will closely reference the provided StartTime metadata. This will help me situate the dialogue within the surrounding context, ensuring the tone, pacing and emotional state of the woman's speech aligns seamlessly with the implied on-screen actions and the male participant's implicit reactions.",
            });
            jtokenIdOverrides.Add(new JTokenIdOverride("AIPrompt", "UserPrompt"));

            dynamic dataExpansionMistralAPI = new ExpandoObject();
            dataExpansionMistralAPI.temperature = 0.7;
            dataExpansionMistralAPI.response_format = new { type = "json_object" };

            dynamic dataExpansionMistral7b = new ExpandoObject();
            dataExpansionMistral7b.max_tokens = 40;

            var config = new SubtitleGeneratorConfig()
            {
                SubtitleForcedTimingsSuffix = ".perfect-vad.srt",
                SharedObjects = new object[]
                {
                    transcriberTool,
                    translatorGoogleV1,
                    systemPromptJson,
                    systemPromptMultiShot,
                    userPrompt
                },
                Transcribers = new Transcriber[]
                {
                            new TranscriberWhisperFullAudio()
                            {
                                TranscriptionId = "full",
                                Translators = new Translator[] { translatorGoogleV1 },
                                TranscriberTool = transcriberTool
                            },
                            new TranscriberWhisperMergedVADAudio()
                            {
                                TranscriptionId = "mergedvad",
                                Translators = new Translator[] {
                                    translatorGoogleV1,
                                    new TranslatorDeepLWithFiles()
                                    {
                                        TranslationId = "deepl",
                                        TargetLanguage = Language.FromString("en")
                                    },
                                    new TranslatorDeepLAPI()
                                    {
                                        Enabled = false,
                                        TranslationId = "deepl-api",
                                        TargetLanguage = Language.FromString("en")
                                    },
                                    new TranslatorAIChatBot()
                                    {
                                        TranslationId = "claude-3-haiku-200k",
                                        TargetLanguage = Language.FromString("en"),
                                        MessagesHandler = new AIMessagesHandlerJson
                                        {
                                            UserPrompt = userPrompt,
                                            MaxItemsInRequest = 10000
                                        }
                                    },
                                    new TranslatorAIChatBot()
                                    {
                                        TranslationId = "mistral-large",
                                        TargetLanguage = Language.FromString("en"),
                                        MessagesHandler = new AIMessagesHandlerJson
                                        {
                                            UserPrompt = userPrompt,
                                            MaxItemsInRequest = 100,
                                            OverlapItemsInRequest = 10
                                        }
                                    },
                                    new TranslatorAIChatBot()
                                    {
                                        Enabled = false,
                                        TranslationId = "chatgpt",
                                        TargetLanguage = Language.FromString("en"),
                                        MessagesHandler = new AIMessagesHandlerJson
                                        {
                                            UserPrompt = userPrompt,
                                            MaxItemsInRequest = 40,
                                            OverlapItemsInRequest = 5
                                        }
                                    },
                                    new TranslatorAIGenericAPI()
                                    {
                                        TranslationId = "local-mistral-7b",
                                        BaseAddress = "http://localhost:10000",
                                        APIKeyName = "MistralAPIKey",
                                        Model = "TheBloke/Mistral-7B-Instruct-v0.2-GGUF/mistral-7b-instruct-v0.2.Q8_0.gguf",
                                        ValidateModelNameInResponse = true,
                                        TargetLanguage = Language.FromString("en"),
                                        DataExpansion = dataExpansionMistral7b,
                                        MessagesHandler = new AIMessagesHandlerMultishot
                                        {
                                            SystemPrompt = systemPromptMultiShot
                                        }
                                    },
                                    new TranslatorAIGenericAPI()
                                    {
                                        Enabled = false,
                                        TranslationId = "mistral-large",
                                        BaseAddress = "https://api.mistral.ai",
                                        APIKeyName = "MistralAPIKey",
                                        Model = "mistral-large-latest",
                                        TargetLanguage = Language.FromString("en"),
                                        DataExpansion = dataExpansionMistralAPI,
                                        MessagesHandler = new AIMessagesHandlerJson
                                        {
                                            MaxItemsInRequest = 100,
                                            OverlapItemsInRequest = 10,
                                            SystemPrompt = systemPromptJson
                                        }
                                    }
                                },
                                TranscriberTool = transcriberTool
                            },
                            new TranscriberWhisperSingleVADAudio()
                            {
                                TranscriptionId = "singlevad",
                                Translators = new Translator[] {
                                    translatorGoogleV1
                                },
                                TranscriberTool = transcriberTool
                            }
                },
                Outputs = new SubtitleOutput[]
                {
                    new SubtitleOutputSimpleSrt()
                    {
                        TranscriptionId = "full",
                        TranslationId = "google",
                        FileSuffixe = ".perfect-vad-potential.srt"
                    },
                    new SubtitleOutputWIPSrt()
                    {
                        TranscriptionOrder = new [] { "singlevad", "mergedvad", "*" },
                        TranslationOrder = new [] { "claude-3-haiku-200k", "mistral-large", "chatgpt", "local-mistral-7b", "deepl", "*" },
                        FileSuffixe = ".wip.srt"
                    }
                }
            };

            return OverridesIdInJObject(JObject.FromObject(config, rs_serializer), jtokenIdOverrides)
                .ToString();
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
        {
            if (token is JObject obj)
            {
                var type = (string)token["$type"];
                if (type?.StartsWith("<>") == true)
                {
                    obj.Remove("$type");
                    type = null;
                }

                var tuppleInstance = replacements.FirstOrDefault(
                    t => t.TypeName == type && t.OldId == null);
                if (tuppleInstance != null)
                {
                    tuppleInstance.OldId = (string)token["$id"];
                    token["$id"] = tuppleInstance.NewId;
                }
                else
                {
                    obj.Remove("$id");
                }

                var reference = (string)token["$ref"];
                if (reference != null)
                {
                    var tuppleReference = replacements.FirstOrDefault(
                        t => t.OldId == reference);
                    if (tuppleReference != null)
                    {
                        token["$ref"] = tuppleReference.NewId;
                    }
                    else
                    {
                        throw new Exception("BUG");
                    }
                }

                foreach (var property in obj.Properties())
                {
                    OverridesIdInJObject(property.Value, replacements); // Recursively call the method for the property value
                }
            }
            else if (token is JArray array)
            {
                for (int i = 0; i < array.Count; i++)
                {
                    OverridesIdInJObject(array[i], replacements); // Recursively call the method for the array item
                }
            }
            else
            {
                // Ignore JValue
            }
            return token;
        }
    }
}