using FunscriptToolbox.Properties;
using FunscriptToolbox.SubtitlesVerbs.AudioExtractions;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using FunscriptToolbox.SubtitlesVerbs.Outputs;
using FunscriptToolbox.SubtitlesVerbs.Transcriptions;
using FunscriptToolbox.SubtitlesVerbs.Translations;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Dynamic;

namespace FunscriptToolbox.SubtitlesVerbs
{
    public static class SubtitleGeneratorConfigExample
    {
        public static string GetExample()
        {
            var jtokenIdOverrides = new List<SubtitleGeneratorConfigLoader.JTokenIdOverride>();
            var sharedObjects = new List<object>();

            T AddSharedObject<T>(string name, T obj) where T : class
            {
                jtokenIdOverrides.Add(new SubtitleGeneratorConfigLoader.JTokenIdOverride(obj.GetType().Name, name));
                sharedObjects.Add(obj);
                return obj;
            }

            AIPrompt AddPromptToSharedObjects(string name, string value)
            {
                var prompt = new AIPrompt(SubtitleGeneratorConfigLoader.CreateLongString(value));
                return AddSharedObject(name, prompt);
            }

            var transcriberToolPurfviewWhisper = AddSharedObject("TranscriberToolPurfviewWhisper", new TranscriberToolAudioPurfviewWhisper
            {
                ApplicationFullPath = @"[TOREPLACE-WITH-PathToPurfview]\Purfview-Whisper-Faster\faster-whisper-xxl.exe",
                Model = "Large-V2",
                ForceSplitOnComma = false
            });

            var aiEngineGPT5 = AddSharedObject("AIEngineGPT5", new AIEngineAPI()
            {
                BaseAddress = "https://api.openai.com/v1",
                Model = "gpt-5",
                APIKeyName = "APIKeyOpenAI",
                RequestBodyExtension = Expando(
                    ("service_tier", "flex")),
                UseStreaming = false
            });
            var aiEngineGPT5ViaPoe = AddSharedObject("AIEngineGPT5ViaPoe", new AIEngineAPI()
            {
                BaseAddress = "https://api.poe.com/v1",
                Model = "GPT-5",
                APIKeyName = "APIKeyPoe"
            });
            var aiEngineGPT5Mini = AddSharedObject("AIEngineGPT5Mini", new AIEngineAPI()
            {
                BaseAddress = "https://api.openai.com/v1",
                Model = "gpt-5-mini",
                APIKeyName = "APIKeyOpenAI",
                RequestBodyExtension = Expando(
                    ("service_tier", "flex")),
                UseStreaming = false
            });
            var aiEngineGPT5MiniViaPoe = AddSharedObject("AIEngineGPT5MiniViaPoe", new AIEngineAPI()
            {
                BaseAddress = "https://api.poe.com/v1",
                Model = "GPT-5-mini",
                APIKeyName = "APIKeyPoe"
            });

            var aiEngineGeminiPro = AddSharedObject("AIEngineGeminiPro", new AIEngineAPI()
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
            });
            var aiEngineGeminiFlash = AddSharedObject("AIEngineGeminiFlash", new AIEngineAPI()
            {
                BaseAddress = "https://generativelanguage.googleapis.com/v1beta/openai/",
                Model = "gemini-2.5-pro-flash",
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
            });

            var aiEngineLocalAPI = AddSharedObject("AIEngineLocalAPI", new AIEngineAPI
            {
                BaseAddress = "http://localhost:10000/v1",
                Model = "mistralai/mistral-small-3.2",
                ValidateModelNameInResponse = true,
                UseStreaming = true
            });

            var transcriberAudioFullSystemPrompt = AddPromptToSharedObjects("TranscriberAudioFullSystemPrompt", Resources.TranscriberAudioFullSystemPrompt);
            var transcriberAudioFullUserPrompt = AddPromptToSharedObjects("TranscriberAudioFullUserPrompt", Resources.TranscriberAudioFullUserPrompt);

            var transcriberAudioSingleVADSystemPrompt = AddPromptToSharedObjects("TranscriberAudioSingleVADSystemPrompt", Resources.TranscriberAudioSingleVADSystemPrompt);
            var transcriberAudioSingleVADUserPrompt = AddPromptToSharedObjects("TranscriberAudioSingleVADUserPrompt", Resources.TranscriberAudioSingleVADUserPrompt);

            var transcriberAudioPrecisionSegmentRefinerSystemPrompt = AddPromptToSharedObjects("TranscriberAudioPrecisionSegmentRefinerSystemPrompt", Resources.TranscriberAudioPrecisionSegmentRefinerSystemPrompt);
            var transcriberAudioPrecisionSegmentRefinerUserPrompt = AddPromptToSharedObjects("TranscriberAudioPrecisionSegmentRefinerUserPrompt", Resources.TranscriberAudioPrecisionSegmentRefinerUserPrompt);

            var transcriberAudioTranscriptionArbitrationRefinementSystemPrompt = AddPromptToSharedObjects("TranscriberAudioTranscriptionArbitrationRefinementSystemPrompt", Resources.TranscriberAudioTranscriptionArbitrationRefinementSystemPrompt);

            var transcriberOnScreenTextSystemPrompt = AddPromptToSharedObjects("TranscriberOnScreenTextSystemPrompt", Resources.TranscriberOnScreenTextSystemPrompt);

            var transcriberVisualAnalystSystemPrompt = AddPromptToSharedObjects("TranscriberVisualAnalystSystemPrompt", Resources.TranscriberVisualAnalystSystemPrompt);
            var transcriberVisualAnalystUserPrompt = AddPromptToSharedObjects("TranscriberVisualAnalystUserPrompt", Resources.TranscriberVisualAnalystUserPrompt);

            var arbitrerSystemPrompt = AddPromptToSharedObjects("ArbitrerSystemPrompt", Resources.ArbitrerSystemPrompt);

            var translatorSystemPrompt = AddPromptToSharedObjects("TranslatorSystemPrompt", Resources.TranslatorSystemPrompt);
            var translatorNaturalistUserPrompt = AddPromptToSharedObjects("TranslatorNaturalistUserPrompt", Resources.TranslatorNaturalistUserPrompt);
            var translatorMaverickUserPrompt = AddPromptToSharedObjects("TranslatorMaverickUserPrompt", Resources.TranslatorMaverickUserPrompt);

            var subtitleFinalizerSystemPrompt = AddPromptToSharedObjects("SubtitleFinalizerSystemPrompt", Resources.SubtitleFinalizerSystemPrompt);
            var subtitleFinalizerUserPrompt = AddPromptToSharedObjects("SubtitleFinalizerUserPrompt", Resources.SubtitleFinalizerUserPrompt);

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
                        Engine = aiEngineGeminiPro,
                        SystemPrompt = transcriberAudioFullSystemPrompt,
                        UserPrompt = transcriberAudioFullUserPrompt,
                        MetadataProduced = "VoiceText",
                        MaxChunkDuration = TimeSpan.FromMinutes(5)
                    },
                    new TranscriberAudioSingleVADAI()
                    {
                        TranscriptionId = "full-ai-refined",
                        SourceAudioId = "audio",
                        PrivateMetadataNames = "Justification",
                        Engine = aiEngineGeminiPro,
                        ExpandStart = TimeSpan.FromSeconds(1.0),
                        ExpandEnd = TimeSpan.FromSeconds(1.0),
                        UpdateTimingsBeforeSaving = true,
                        AddSpeechCadenceBeforeSaving = true,
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "full-ai",
                            Sources = "full-ai",
                            MergeRules = new Dictionary<string, string>
                            {
                                { "VoiceText", "OriginalVoiceText" }
                            }
                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = transcriberAudioPrecisionSegmentRefinerSystemPrompt,
                            UserPrompt = transcriberAudioPrecisionSegmentRefinerUserPrompt,
                            MetadataNeeded = "OriginalVoiceText",
                            MetadataAlwaysProduced = "VoiceText",
                            FieldsToInclude = NodeFields.StartTime,
                            BatchSize = 50,
                            BatchSplitWindows = 0,
                            NbContextItems = 0,
                            NbItemsMinimumReceivedToContinue = 30
                        }
                    },
                    new TranscriberClone()
                    {
                        TranscriptionId = "full",
                        SourceId = "NEED-TO-BE-OVERRIDED" // Should be full-whisper or full-ai or full-ai-refined
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

                            BatchSize = 20,
                            BatchSplitWindows = 0,
                            NbContextItems = 10,
                            NbItemsMinimumReceivedToContinue = 10,
                            FieldsToInclude = NodeFields.StartTime
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
                        FillGapSmallerThen = TimeSpan.FromSeconds(0.2),
                        Engine = aiEngineGeminiPro,
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

                            BatchSize = 100,
                            BatchSplitWindows = 5,
                            NbContextItems = 100,
                            NbItemsMinimumReceivedToContinue = 50,
                            FieldsToInclude = NodeFields.StartTime | NodeFields.EndTime
                        }
                    },
                    new TranscriberAudioSingleVADAI()
                    {
                        TranscriptionId = "singlevad-ai-refined",
                        SourceAudioId = "audio",
                        PrivateMetadataNames = "Justification",
                        FillGapSmallerThen = TimeSpan.FromSeconds(0.2),
                        Engine = aiEngineGeminiPro,
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "singlevad-ai",
                            Sources = "singlevad-ai,full-ai,manual-input",
                            MergeRules = new Dictionary<string, string>
                            {
                                { "singlevad-ai,VoiceText", "singlevad-VoiceText" },
                                { "full-ai,VoiceText", "full-VoiceText" }
                            }
                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = transcriberAudioTranscriptionArbitrationRefinementSystemPrompt,
                            MetadataNeeded = "singlevad-VoiceText",
                            MetadataAlwaysProduced = "VoiceText",

                            BatchSize = 100,
                            BatchSplitWindows = 0,
                            NbContextItems = 50,
                            NbItemsMinimumReceivedToContinue = 30,
                            FieldsToInclude = NodeFields.StartTime | NodeFields.EndTime
                        }
                    },
                    new TranscriberClone()
                    {
                        TranscriptionId = "voice-texts",
                        SourceId = "NEED-TO-BE-OVERRIDED" // Should be full-ai or singlevad-ai or singlevad-ai-refined
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
                        Engine = aiEngineGeminiPro,
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "timings",
                            Sources = "manual-input"
                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = transcriberOnScreenTextSystemPrompt,
                            MetadataNeeded = "GrabOnScreenText",
                            MetadataAlwaysProduced = "OnScreenText",

                            BatchSize = 30,
                            BatchSplitWindows = 0,
                            NbContextItems = 0,
                            NbItemsMinimumReceivedToContinue = 20,
                            FieldsToInclude = NodeFields.StartTime
                        }
                    },
                    new TranscriberImageAI()
                    {
                        TranscriptionId = "visual-analysis",
                        PrivateMetadataNames = "VoiceText",
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
                            MetadataAlwaysProduced = "TranslationAnalysis-Visual",
                            MetadataForTraining = "VisualTraining",

                            BatchSize = 30,
                            BatchSplitWindows = 0,
                            NbContextItems = 5,
                            NbItemsMinimumReceivedToContinue = 10,
                            FieldsToInclude = NodeFields.StartTime
                        }
                    },
                    new SubtitleOutputComplexSrt()
                    {
                        OutputId = "metadatas-srt",
                        FileSuffix = ".metadatas.srt",
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "timings",
                            Sources = "visual-analysis,voice-texts,on-screen-texts,speakers,manual-input",
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
                            Sources = "visual-analysis,on-screen-texts,voice-texts,speakers,manual-input"
                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = translatorSystemPrompt,
                            UserPrompt = translatorMaverickUserPrompt,
                            TextAfterAnalysis = " --reasoning_effort medium",

                            MetadataNeeded = "VoiceText|OnScreenText",
                            MetadataAlwaysProduced = "TranslatedText",

                            BatchSize = 150,
                            BatchSplitWindows = 10,
                            NbContextItems = 10000,
                            MetadataInContextLimits = new Dictionary<string, int>
                            {
                                { "ParticipantsPoses", 10 },
                                { "TranslationAnalysis-Audio", 10 },
                                { "TranslationAnalysis-Visual", 10 },
                                { "VoiceText", 10 }
                            },
                            NbItemsMinimumReceivedToContinue = 50,
                            FieldsToInclude = NodeFields.StartTime | NodeFields.EndTime
                        }
                    },
                    new TranslatorAI()
                    {
                        TranslationId = "finalized_maverick",
                        TargetLanguage = Language.FromString("en"),
                        Engine = aiEngineGeminiPro,
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "timings",
                            Sources = "translated-texts_maverick,speakers,manual-input",
                            MergeRules = new Dictionary<string, string>()
                            {
                                { "TranslatedText", "OriginalTranslatedText" }
                            }
                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = subtitleFinalizerSystemPrompt,
                            UserPrompt = subtitleFinalizerUserPrompt,

                            MetadataNeeded = "OriginalTranslatedText",
                            MetadataAlwaysProduced = "TranslatedText",

                            BatchSize = 300,
                            BatchSplitWindows = 10,
                            NbContextItems = 100,
                            NbItemsMinimumReceivedToContinue = 50,
                            FieldsToInclude = NodeFields.StartTime | NodeFields.EndTime
                        },
                        AutoMergeOn = "[!MERGED]",
                        AutoDeleteOn = "[!UNNEEDED]"
                    },
                    new TranslatorAI()
                    {
                        TranslationId = "translated-texts_naturalist",
                        TargetLanguage = Language.FromString("en"),
                        Engine = aiEngineGPT5ViaPoe,
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "timings",
                            Sources = "visual-analysis,on-screen-texts,voice-texts,speakers,manual-input"
                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = translatorSystemPrompt,
                            UserPrompt = translatorNaturalistUserPrompt,
                            TextAfterAnalysis = " --reasoning_effort medium",

                            MetadataNeeded = "VoiceText|OnScreenText",
                            MetadataAlwaysProduced = "TranslatedText",

                            BatchSize = 150,
                            BatchSplitWindows = 10,
                            NbContextItems = 1000,
                            MetadataInContextLimits = new Dictionary<string, int>
                            {
                                { "ParticipantsPoses", 10 },
                                { "TranslationAnalysis-Audio", 10 },
                                { "TranslationAnalysis-Visual", 10 },
                                { "VoiceText", 10 }
                            },
                            NbItemsMinimumReceivedToContinue = 50,
                            FieldsToInclude = NodeFields.StartTime | NodeFields.EndTime
                        }
                    },
                    new TranscriberAggregator
                    {
                        TranscriptionId = "arbitrer-choices",
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "timings"
                        },
                        CandidatesSources = "translated-texts_maverick,translated-texts_naturalist,voice-texts,on-screen-texts,mergedvad,full",
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
                            Sources = "visual-analysis,voice-texts,on-screen-texts,speakers,arbitrer-choices,manual-input"
                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = arbitrerSystemPrompt,
                            TextAfterAnalysis = " --reasoning_effort medium",

                            MetadataNeeded = "CandidatesText",
                            MetadataAlwaysProduced = "FinalText",


                            BatchSize = 150,
                            BatchSplitWindows = 10,
                            NbContextItems = 100,
                            MetadataInContextLimits = new Dictionary<string, int>
                            {
                                { "ParticipantsPoses", 10 },
                                { "TranslationAnalysis-Audio", 10 },
                                { "TranslationAnalysis-Visual", 10 },
                                { "VoiceText", 10 }
                            },
                            NbItemsMinimumReceivedToContinue = 50,
                            FieldsToInclude = NodeFields.StartTime | NodeFields.EndTime
                        },
                        AutoMergeOn = "[!MERGED]",
                        AutoDeleteOn = "[!UNNEEDED]"
                    },
                    new TranscriberClone()
                    {
                        TranscriptionId = "final-ai-subtitles",
                        SourceId = "NEED-TO-BE-OVERRIDED" // Should be finalized_maverick or arbitrer-final-choice
                    },
                    new SubtitleOutputSimpleSrt()
                    {
                        OutputId = "final-candidate-srt",
                        WorkerId = "final-ai-subtitles",
                        FileSuffix = ".final-candidate.srt",
                        MinimumSubtitleDuration = TimeSpan.FromSeconds(1.5),
                        ExpandSubtileDuration = TimeSpan.FromSeconds(0.5),
                        SaveFullFileToo = true
                    },
                    new SubtitleOutputAsig()
                    {
                        OutputId = "asig",
                        SourceAudioId = "audio",
                        FileSuffix = ".asig",
                        SaveFullFileToo = true
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
                            TimingsSource = "final-ai-subtitles",
                            Sources = "voice-texts,on-screen-texts,visual-analysis,speakers,manual-input",
                        },
                        TextSources = "final-user-edited,final-ai-subtitles"
                    }
                }
            };

            return SubtitleGeneratorConfigLoader.ReplaceLongStringFromJsonToHybrid(
                SubtitleGeneratorConfigLoader.OverridesIdInJObject(JObject.FromObject(config, SubtitleGeneratorConfigLoader.rs_serializer), jtokenIdOverrides)
                .ToString());
        }

        static ExpandoObject Expando(params (string key, object value)[] items)
        {
            var e = new ExpandoObject();
            var dict = (IDictionary<string, object>)e;
            foreach (var (k, v) in items) dict[k] = v!;
            return e;
        }
    }
}