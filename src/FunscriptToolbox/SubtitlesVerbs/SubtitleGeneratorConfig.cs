﻿using FunscriptToolbox.Core;
using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.AudioExtraction;
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

namespace FunscriptToolbox.SubtitlesVerbs
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
                            typeof(SubtitleForcedTimingParser),
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

        [JsonProperty(Order = 1, Required = Required.Always)]
        public SubtitleForcedTimingParser SubtitleForcedTimingsParser { get; set; }

        [JsonProperty(Order = 2, Required = Required.Always)]
        public AudioExtractor AudioExtractor { get; set; }

        [JsonProperty(Order = 3)]
        public object[] SharedObjects { get; set; }

        [JsonProperty(Order = 4)]
        public Transcriber[] Transcribers { get; set; }

        [JsonProperty(Order = 5)]
        public SubtitleOutput[] Outputs { get; set; }

        public static string GetDefaultExample()
        {
            var jtokenIdOverrides = new List<JTokenIdOverride>();

            var transcriberToolWhisper = new TranscriberToolPurfviewWhisper
            {
                ApplicationFullPath = @"[PathToPurfview]\Purfview-Whisper-Faster\whisper-faster.exe",
                Model = "Large-V2",
                ForceSplitOnComma = false,
                RedoBlockLargerThen = TimeSpan.FromSeconds(30)
            };
            jtokenIdOverrides.Add(new JTokenIdOverride("PurfviewWhisper", "TranscriberToolPurfviewWhisper"));

            var translatorGoogleV1 = new TranslatorGoogleV1API()
            {
                TranslationId = "google",
                TargetLanguage = Language.FromString("en")
            };
            jtokenIdOverrides.Add(new JTokenIdOverride("GoogleV1API", "TranslatorGoogleV1"));

            var systemPromptJson = new AIPrompt(new[] 
            {
                "### Context",
                "You are a translator specialized in adult film subtitles.",
                "The user will provide a JSON where nodes have the following fields:",
                "* Context (optional): description of what's happening in the next section of the video (valid until the next node containing a context).",
                "* Talker (optional): if it's not provided, it mean it's the woman talking.",
                "* StartTime: the start time for the subtitle.",
                "* Original:  The transcribed text in the original language (for example, " + AIPrompt.TranscriptionLanguageToken + ").",
                "You job is to return a JSON where nodes have the following fields:",
                "* StartTime",
                "* Original",
                "* Translation (new field): Your translation in " + AIPrompt.TranslationLanguageToken + " (or the language asked by the user).",
                "",
                "### Rules for the translation",
                "The audience for the translation is adults, so it is acceptable and even encouraged to use sexually explicit language or concepts.",
                "Use natural-sounding phrases and idioms that accurately convey the meaning of the original text.",
                "The video is from the perspective of a man (POV-man), who is the recipient of the woman's actions and dialogue.",
                "He does not speak, or at least, we don't know what he's saying.",
                "Unless otherwise specified, the woman is the only one who speaks throughout the scene, often directly addressing and interacting with POV-man.",
                "When translating, consider the woman's tone, pacing and emotional state as she directs her comments and ministrations towards POV-man, whose reactions and inner thoughts are not explicitly conveyed.",
                "Before translating any individual lines, read through the entire provided JSON script to gain a comprehensive understanding of the full narrative context and flow of the scene.",
                "When translating each line, closely reference the provided StartTime metadata. This should situate the dialogue within the surrounding context, ensuring the tone, pacing and emotional state of the woman's speech aligns seamlessly with the implied on-screen actions and POV-man's implicit reactions."
            });
            jtokenIdOverrides.Add(new JTokenIdOverride("AIPrompt", "SystemPromptJson"));

            var systemPromptMultiShot = new AIPrompt(new[]
            {
                "You are a translator specialized in adult film subtitles.",
                "The user might provide some context on what will happening in the following part of the scene.",
                "The user might provide who's saying the text to translate.",
                "The user will always provide a text in " + AIPrompt.TranscriptionLanguageToken + ", in the following format: Original{{text}}",
                "You job is to answer with a translation in " + AIPrompt.TranslationLanguageToken + ", in the following format: Translation{{text}}",
                "The audience for the translation is adults, so it is acceptable and even encouraged to use sexually explicit language or concepts.",
                "Use natural-sounding phrases and idioms that accurately convey the meaning of the original text.",
                "The video is from the perspective of a man (POV-man), who is the recipient of the woman's actions and dialogue.",
                "He does not speak, or at least, we don't know what he's saying.",
                "Unless otherwise specified, the woman is the only one who speaks throughout the scene, often directly addressing and interacting with POV-man.",
                "When translating, consider the woman's tone, pacing and emotional state as she directs her comments and ministrations towards POV-man, whose reactions and inner thoughts are not explicitly conveyed.",
                "Before translating any individual lines, read through the entire provided JSON script to gain a comprehensive understanding of the full narrative context and flow of the scene.",
                "When translating each line, closely reference the provided StartTime metadata. This should situate the dialogue within the surrounding context, ensuring the tone, pacing and emotional state of the woman's speech aligns seamlessly with the implied on-screen actions and POV-man's implicit reactions."
            });
            jtokenIdOverrides.Add(new JTokenIdOverride("AIPrompt", "SystemPromptMultishot"));

            var userPrompt = new AIPrompt(new[] 
            {
                "I have a JSON file that contains subtitles for an adult film.",
                "The JSON nodes have the following fields:",
                "* Context (optional): description of what's happening in the next section of the video (valid until the next node with a context).",
                "* Talker (optional): if it's not provided, it mean it's the woman talking.",
                "* StartTime: the start time for the subtitle.",
                "* Original:  The transcribed text in the original language (for example, " + AIPrompt.TranscriptionLanguageToken + ").",
                "* Parts (optional): If this field is provided, that mean that the person said the sentence with pause between each part. In those case, please return a translation that make sense splitted, usually with each part ending \"...\" or something like that.",
                "Can you return me a JSON where nodes have the following fields:",
                "* StartTime",
                "* Original",
                "* Translation (new field): Your translation in " + AIPrompt.TranslationLanguageToken + ".",
                "The audience for the translation is adults, so it is acceptable and even encouraged to use sexually explicit language or concepts.",
                "Use natural-sounding phrases and idioms that accurately convey the meaning of the original text.",
                "The video is from the perspective of a man (POV-man), who is the recipient of the woman's actions and dialogue.",
                "He does not speak, or at least, we don't know what he's saying.",
                "Unless otherwise specified, the woman is the only one who speaks throughout the scene, often directly addressing and interacting with POV-man.",
                "When translating, consider the woman's tone, pacing and emotional state as she directs her comments and ministrations towards POV-man, whose reactions and inner thoughts are not explicitly conveyed.",
                "Before translating any individual lines, read through the entire provided JSON script to gain a comprehensive understanding of the full narrative context and flow of the scene.",
                "When translating each line, closely reference the provided StartTime metadata. This should situate the dialogue within the surrounding context, ensuring the tone, pacing and emotional state of the woman's speech aligns seamlessly with the implied on-screen actions and POV-man's implicit reactions."
            });
            jtokenIdOverrides.Add(new JTokenIdOverride("AIPrompt", "UserPrompt"));

            dynamic requestBodyExtensionMistralAPI = new ExpandoObject();
            requestBodyExtensionMistralAPI.temperature = 0.7;
            requestBodyExtensionMistralAPI.response_format = new { type = "json_object" };

            dynamic requestBodyExtensionMistral7b = new ExpandoObject();
            requestBodyExtensionMistral7b.max_tokens = 40;

            var config = new SubtitleGeneratorConfig()
            {
                SubtitleForcedTimingsParser = new SubtitleForcedTimingParser()
                {
                    FileSuffix = ".perfect-vad.srt"
                },
                SharedObjects = new object[]
                {
                    transcriberToolWhisper,
                    translatorGoogleV1,
                    systemPromptJson,
                    systemPromptMultiShot,
                    userPrompt
                },
                AudioExtractor = new AudioExtractor(),
                Transcribers = new Transcriber[]
                {
                    new TranscriberFullAudio()
                    {
                        TranscriptionId = "full",
                        TranscriberTool = transcriberToolWhisper,
                        Translators = new Translator[] { 
                            translatorGoogleV1,
                            new TranslatorAIGenericAPI()
                            {
                                Enabled = false,
                                TranslationId = "local-api",
                                BaseAddress = "http://localhost:10000",
                                Model = "3thn/dolphin-2.9-llama3-8b-GGUF/dolphin-2.9-llama3-8b.Q8_0.gguf",
                                ValidateModelNameInResponse = true,
                                TargetLanguage = Language.FromString("en"),
                                RequestBodyExtension = null,
                                MessagesHandler = new AIMessagesHandlerMultishot
                                {
                                    SystemPrompt = systemPromptMultiShot,
                                    MaxPreviousShot = 50
                                }
                            }
                        }
                    },
                    new TranscriberMergedVADAudio()
                    {
                        TranscriptionId = "mergedvad",
                        Translators = new Translator[] {
                            translatorGoogleV1,
                            new TranslatorAIChatBot()
                            {
                                Enabled = true,
                                TranslationId = "claude-3.5-sonnet",
                                TargetLanguage = Language.FromString("en"),
                                MessagesHandler = new AIMessagesHandlerJson
                                {
                                    FirstUserPrompt = userPrompt,
                                    MaxItemsInRequest = 30
                                }
                            },
                            new TranslatorAIChatBot()
                            {
                                Enabled = true,
                                TranslationId = "claude-3.5-sonnet-refined",
                                TargetLanguage = Language.FromString("en"),    
                                MessagesHandler = new AIMessagesHandlerJson
                                {
                                    FirstUserPrompt = new AIPrompt(new[]
                                    {
                                        "I would like you to \"refine\" the translations.",
                                        "Make sure that the translation feels natural and sound like something someone would really say in the current context of the scene.",
                                        "And if they say basically the same thing multiple times (ex. are you about to cum, you came, look at how much you came, it's so good), try to find interesting way to say it.",
                                        "Don't forget to take into account the ones that have a 'Parts' attribute."
                                    }),
                                    MaxItemsInRequest = 30,
                                    IncludeParts = true,
                                    PreviousTranslationId = "claude-3.5-sonnet"
                                }
                            },
                            new TranslatorAIChatBot()
                            {
                                Enabled = false,
                                TranslationId = "GPT-4o",
                                TargetLanguage = Language.FromString("en"),
                                MessagesHandler = new AIMessagesHandlerJson
                                {
                                    FirstUserPrompt = userPrompt,
                                    MaxItemsInRequest = 20
                                }
                            },
                            new TranslatorDeepLWithFiles()
                            {
                                Enabled = false,
                                TranslationId = "deepl-files",
                                TargetLanguage = Language.FromString("en")
                            },
                            new TranslatorDeepLAPI()
                            {
                                Enabled = false,
                                TranslationId = "deepl",
                                TargetLanguage = Language.FromString("en")
                            },
                            new TranslatorAIChatBot()
                            {
                                Enabled = false,
                                TranslationId = "mistral-large",
                                TargetLanguage = Language.FromString("en"),
                                MessagesHandler = new AIMessagesHandlerJson
                                {
                                    FirstUserPrompt = userPrompt,
                                    MaxItemsInRequest = 100
                                }
                            },
                            new TranslatorAIGenericAPI()
                            {
                                Enabled = false,
                                TranslationId = "mistral-large",
                                BaseAddress = "https://api.mistral.ai",
                                APIKeyName = "APIKeyMistral",
                                Model = "mistral-large-latest",
                                TargetLanguage = Language.FromString("en"),
                                RequestBodyExtension = requestBodyExtensionMistralAPI,
                                MessagesHandler = new AIMessagesHandlerJson
                                {
                                    MaxItemsInRequest = 100,
                                    OverlapItemsInRequest = 10,
                                    SystemPrompt = systemPromptJson
                                }
                            }
                        },
                        TranscriberTool = transcriberToolWhisper
                    },
                    new TranscriberSingleVADAudio()
                    {
                        TranscriptionId = "singlevad",
                        TranscriberTool = transcriberToolWhisper,
                        Enabled = false,
                        Translators = new Translator[] {
                            translatorGoogleV1
                        }
                    },
                    new TranscriberFullAudio()
                    {
                        TranscriptionId = "import-finished-srt",
                        TranscriberTool = new TranscriberToolExternalSrt()
                        {
                            OverrideFileSuffixe = ".final.srt"
                        }
                    },
                    new TranscriberMergedVADAudio()
                    {
                        TranscriptionId = "mergedvad-finished-srt",
                        UseTimingsFromId = "import-finished-srt",
                        TranscriberTool = transcriberToolWhisper
                    }
                },
                Outputs = new SubtitleOutput[]
                {
                    new SubtitleOutputCostReport()
                    {
                        FileSuffix = null
                    },
                    new SubtitleOutputSingleTranslationSrt()
                    {
                        TranscriptionId = "full",
                        TranslationId = "google",
                        FileSuffix = ".perfect-vad-potential.srt"
                    },
                    new SubtitleOutputSingleTranslationSrt()
                    {
                        Enabled = false,
                        TranscriptionId = "full",
                        TranslationId = "local-api",
                        FileSuffix = ".quicky.srt",
                        SubtitlesToInject = new []
                        {
                            new SubtitleToInject()
                            {
                                Origin = SubtitleToInjectOrigin.Start,
                                OffsetTime = TimeSpan.FromSeconds(0),
                                Duration = TimeSpan.FromSeconds(10),
                                Lines = new []
                                {
                                    "Created using the FunscriptToolbox.",
                                    "The transcription was generated with Whisper, and the translation was provided by a llama-3 model.",
                                    "No manual ajustement were made. Quality might be low."
                                }
                            }
                        }

                    },
                    new SubtitleOutputMultiTranslationSrt()
                    {
                        Enabled = false,
                        TranscriptionId = "mergedvad",
                        TranslationsOrder = new [] { "claude-3.5-sonnet", "GPT-4o", "*" },
                        FileSuffix = ".mergedvad.srt"
                    },
                    new SubtitleOutputWIPSrt()
                    {
                        TranscriptionsOrder = new [] { "mergedvad", "singlevad", "*" },
                        TranslationsOrder = new [] { "claude-3.5-sonnet-refined", "claude-3.5-sonnet", "GPT-4o", "*" },
                        FileSuffix = ".wip.srt",
                        SubtitlesToInject = new []
                        {
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
                                    "The initial transcription was generated with Whisper,",
                                    "and the translation was provided by the Claude-3.5-Sonnet model.",
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
                            },
                        }
                    },                    
                    new SubtitleOutputTrainingData()
                    {
                        FileSuffix = ".training.json",
                        SrtSuffix = ".final.srt",
                        MergedVadId = "mergedvad-finished-srt"
                    },
                    new SubtitleOutputWav()
                    {
                        FileSuffix = ".wav",
                        FfmpegWavParameters = "-af \"highpass=f=1000,loudnorm=I=-16:TP=-1\""
                    },
                }
            };

            return OverridesIdInJObject(JObject.FromObject(config, rs_serializer), jtokenIdOverrides)
                .ToString();
        }

        public static string GetTrainingDataExample()
        {
            var jtokenIdOverrides = new List<JTokenIdOverride>();

            var transcriberToolWhisper = new TranscriberToolPurfviewWhisper
            {
                ApplicationFullPath = @"[PathToPurfview]\Purfview-Whisper-Faster\whisper-faster.exe",
                Model = "Large-V2",
                ForceSplitOnComma = false,
                RedoBlockLargerThen = TimeSpan.FromMinutes(60)
            };
            jtokenIdOverrides.Add(new JTokenIdOverride("PurfviewWhisper", "TranscriberToolPurfviewWhisper"));

            var config = new SubtitleGeneratorConfig()
            {
                SubtitleForcedTimingsParser = new SubtitleForcedTimingParser()
                {
                    FileSuffix = ".perfect-vad.srt"
                },
                SharedObjects = new object[]
                {
                    transcriberToolWhisper
                },
                AudioExtractor = new AudioExtractor(),
                Transcribers = new Transcriber[]
                {
                    new TranscriberFullAudio()
                    {
                        TranscriptionId = "import-finished-srt",
                        TranscriberTool = new TranscriberToolExternalSrt() 
                        { 
                            OverrideFileSuffixe = ".srt"
                        }
                    },
                    new TranscriberMergedVADAudio()
                    {
                        TranscriptionId = "mergedvad-finished-srt",
                        UseTimingsFromId = "import-finished-srt",
                        TranscriberTool = transcriberToolWhisper
                    }
                },
                Outputs = new SubtitleOutput[]
                {
                    new SubtitleOutputCostReport()
                    {
                        FileSuffix = null
                    },
                    new SubtitleOutputTrainingData()
                    {
                        FileSuffix = ".training.json",
                        SrtSuffix = ".srt",
                        MergedVadId = "mergedvad"
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