using FunscriptToolbox.SubtitlesVerbsV2.Outputs;
using FunscriptToolbox.SubtitlesVerbsV2.Transcriptions;
using FunscriptToolbox.SubtitlesVerbsV2.Translations;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.IO;

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
                            typeof(SubtitleOutput),
                            typeof(TranscriberTool),
                            typeof(Transcriber),
                            typeof(Translator)
                        }),
                    TypeNameHandling = TypeNameHandling.Auto        
                });
        static SubtitleGeneratorConfig()
        {
            rs_serializer.Converters.Add(new StringEnumConverter());
        }

        public static SubtitleGeneratorConfig FromFile(string filepath)
        {
            using var reader = File.OpenText(filepath);
            using var jsonReader = new JsonTextReader(reader);
            return rs_serializer.Deserialize<SubtitleGeneratorConfig>(jsonReader);
        }

        public SubtitleGeneratorConfig()
        {
        }

        [JsonProperty(Order = 1)]
        public string SubtitleForcedLocationSuffix { get; set; }

        [JsonProperty(Order = 2)]
        public string FfmpegAudioExtractionParameters { get; set; }

        [JsonProperty(Order = 3)]
        public Transcriber[] Transcribers { get; set; }

        [JsonProperty(Order = 4)]
        public SubtitleOutput[] Outputs { get; set; }

        // Only to create default config on installation
        internal string GetFileContent()
        {
            using var writer = new StringWriter();
            rs_serializer.Serialize(writer, this);
            return writer.ToString();
        }

        public static SubtitleGeneratorConfig GetExample()
        {
            var transcriberTool = new TranscriberToolPurfviewWhisper
            {
                ApplicationFullPath = @"[PathToPurfview]\Purfview-Whisper-Faster\whisper-faster.exe",
                Model = "Large-V2",
                ForceSplitOnComma = true,
                RedoBlockLargerThen = TimeSpan.FromSeconds(15)
            };

            var config = new SubtitleGeneratorConfig()
            {
                SubtitleForcedLocationSuffix = ".perfect-vad.srt",
                Transcribers = new Transcriber[]

                {
                            new TranscriberWhisperFullAudio()
                            {
                                TranscriptionId = "f",
                                Translators = new Translator[] {
                                    new TranslatorGoogleV1() 
                                    { 
                                        TranslationId = "g",
                                        TargetLanguage = Language.FromString("en") 
                                    }
                                },
                                TranscriberTool = transcriberTool
                            },
                            new TranscriberWhisperMergedVADAudio()
                            {
                                TranscriptionId = "mv",
                                Translators = new Translator[] {
                                    new TranslatorGoogleV1() 
                                    {                         
                                        TranslationId = "g",
                                        TargetLanguage = Language.FromString("en") 
                                    },
                                    new TranslatorChatBotAI()
                                    {
                                        TranslationId = "claude-3-Haiku-200k-bot-v2",
                                        OthersTranslationId = new [] { "claude-3-opus", "chatgpt-4" },
                                        TargetLanguage = Language.FromString("en"),
                                        MessagesHandler = new AIMessagesHandlerBigRequest
                                        {
                                            MaxItemsInRequest = 100,
                                            OverlapItemsInRequest = 10,
                                            UserPrompt = new []
                                            {
                                                "I have a JSON file that contain subtitles for an adult film.",
                                                "Each node of the JSON have a start time and the original text in " + AIMessagesHandler.TranscriptionLanguageToken + ".",
                                                "Can you give me a JSON where you added an 'Translation' field on each node that contains an " + AIMessagesHandler.TranslationLanguageToken + " translation.",
                                                "The audience for the translation is adults so it is acceptable to use explicitily sexual words or concepts.",
                                                "Use natural-sounding phrases and idioms that accurately convey the meaning of the original text.",
                                                "The subtitles are from a VR video where only the girl talk.",
                                                "Use the text surrounding the node to better understand the context and create a better translation.",
                                                "The StartTime field could also help you considerer more or less the surrounding texts, according to the time difference."
                                            }
                                        }
                                    },
                                    new TranslatorChatBotAI()
                                    {
                                        TranslationId = "claude-3-Haiku-200k",
                                        OthersTranslationId = new [] { "claude-3-opus", "chatgpt-4" },
                                        TargetLanguage = Language.FromString("en"),
                                        MessagesHandler = new AIMessagesHandlerBigRequest
                                        {
                                            MaxItemsInRequest = 100,
                                            OverlapItemsInRequest = 10,
                                            UserPrompt = new []
                                            {
                                                "I have a JSON file that contain subtitles for an adult film.",
                                                "Each node of the JSON have a start time and the original text in " + AIMessagesHandler.TranscriptionLanguageToken + ".",
                                                "Can you give me a JSON where you added an 'Translation' field on each node that contains an " + AIMessagesHandler.TranslationLanguageToken + " translation.",
                                                "The audience for the translation is adults so it is acceptable to use explicitily sexual words or concepts.",
                                                "Use natural-sounding phrases and idioms that accurately convey the meaning of the original text.",
                                                "The subtitles are from a VR video where only the girl talk.",
                                                "Use the text surrounding the node to better understand the context and create a better translation.",
                                                "The StartTime field could also help you considerer more or less the surrounding texts, according to the time difference."
                                            }
                                        }
                                    },
                                    new TranslatorGenericOpenAIAPI()
                                    {
                                        Enabled = false,
                                        TranslationId = "mistral-large",
                                        BaseAddress = "https://api.mistral.ai",
                                        APIKey = "[InsertYourAPIKeyHere]",
                                        Model = "mistral-large-latest",
                                        TargetLanguage = Language.FromString("en"),
                                        MessagesHandler = new AIMessagesHandlerBigRequest
                                        {
                                            MaxItemsInRequest = 100,
                                            OverlapItemsInRequest = 10,
                                            SystemPrompt = new [] {
                                                "You are translator specialized in adult film subtitles.",
                                                "The user will provide a JSON where nodes have a start time, original text and, sometime, description of what's happening in the following part of the video.",
                                                "You job is to add a 'Translation' field to each node with a " + AIMessagesHandler.TranscriptionLanguageToken + ".",
                                                "The audience for the translation is adults so it is acceptable to use explicitily sexual words or concepts.",
                                                "Use natural-sounding phrases and idioms that accurately convey the meaning of the original text.",
                                                "The video is from the perspective of the male participant, who is the passive recipient of the woman's actions and dialogue. He does not speak or initiate any of the activities.",
                                                "The woman is the only one who speaks throughout the scene, often directly addressing and interacting with the male participant.",
                                                "When translating, consider the woman's tone, pacing and emotional state as she directs her comments and ministrations towards the passive male participant, whose reactions and inner thoughts are not explicitly conveyed.",
                                                "Before translating any individual lines, I will first read through the entire provided JSON script to gain a comprehensive understanding of the full narrative context and flow of the scene. This will allow me to consider how each line contributes to the overall progression and tone.",
                                                "When translating each line, I will closely reference the provided StartTime metadata. This will help me situate the dialogue within the surrounding context, ensuring the tone, pacing and emotional state of the woman's speech aligns seamlessly with the implied on-screen actions and the male participant's implicit reactions.",
                                            }
                                        }
                                    }
                                },
                                TranscriberTool = transcriberTool
                            },
                            new TranscriberWhisperSingleVADAudio()
                            {
                                TranscriptionId = "sv",
                                Translators = new Translator[] {
                                    new TranslatorGoogleV1() 
                                    { 
                                        TranslationId = "g",
                                        TargetLanguage = Language.FromString("en") 
                                    }
                                },
                                TranscriberTool = transcriberTool
                            }
                },
                Outputs = new SubtitleOutput[]
                {
                    new SubtitleOutputSimpleSrt()
                    {
                        TranscriptionId = "f",
                        TranslationId = "google",
                        FileSuffixe = ".perfect-vad-potential.srt"
                    },
                    new SubtitleOutputWIPSrt()
                    {
                        TranscriptionOrder = new [] { "sv", "mv", "*" },
                        TranslationOrder = new [] { "claude-3-Haiku-200k", "mistral-large", "*" },
                        FileSuffixe = ".wip.srt"
                    }
                }
            };

            return config;
        }
    }
}