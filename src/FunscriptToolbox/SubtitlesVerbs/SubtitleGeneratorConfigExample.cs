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

            // All engine should be overriden in --FSTB-SubtitleGenerator.override.txt
            var defaultEngine = AddSharedObject("ChatBotAIEngineWillBeOverriden", new AIEngineChatBot());            

            var transcriberAudioFullSystemPrompt = AddPromptToSharedObjects(nameof(Resources.TranscriberAudioFullSystemPrompt), Resources.TranscriberAudioFullSystemPrompt);
            var transcriberAudioFullUserPrompt = AddPromptToSharedObjects(nameof(Resources.TranscriberAudioFullUserPrompt), Resources.TranscriberAudioFullUserPrompt);

            var transcriberAudioPrecisionSegmentRefinerSystemPrompt = AddPromptToSharedObjects(nameof(Resources.TranscriberAudioFullTimingRefinerSystemPrompt), Resources.TranscriberAudioFullTimingRefinerSystemPrompt);
            var transcriberAudioPrecisionSegmentRefinerUserPrompt = AddPromptToSharedObjects(nameof(Resources.TranscriberAudioFullTimingRefinerUserPrompt), Resources.TranscriberAudioFullTimingRefinerUserPrompt);

            var transcriberAudioSingleVADSystemPrompt = AddPromptToSharedObjects(nameof(Resources.TranscriberAudioSingleVADSystemPrompt), Resources.TranscriberAudioSingleVADSystemPrompt);
            var transcriberAudioSingleVADUserPrompt = AddPromptToSharedObjects(nameof(Resources.TranscriberAudioSingleVADUserPrompt), Resources.TranscriberAudioSingleVADUserPrompt);

            var transcriberAudioSingleVADRefinerSystemPrompt = AddPromptToSharedObjects(nameof(Resources.TranscriberAudioSingleVADRefinerSystemPrompt), Resources.TranscriberAudioSingleVADRefinerSystemPrompt);
            var transcriberAudioSingleVADRefinerUserPrompt = AddPromptToSharedObjects(nameof(Resources.TranscriberAudioSingleVADRefinerUserPrompt), Resources.TranscriberAudioSingleVADRefinerUserPrompt);

            var transcriberOnScreenTextSystemPrompt = AddPromptToSharedObjects(nameof(Resources.TranscriberOnScreenTextSystemPrompt), Resources.TranscriberOnScreenTextSystemPrompt);

            var transcriberVisualAnalystSystemPrompt = AddPromptToSharedObjects(nameof(Resources.TranscriberVisualAnalystSystemPrompt), Resources.TranscriberVisualAnalystSystemPrompt);
            var transcriberVisualAnalystUserPrompt = AddPromptToSharedObjects(nameof(Resources.TranscriberVisualAnalystUserPrompt), Resources.TranscriberVisualAnalystUserPrompt);

            var translatorSystemPrompt = AddPromptToSharedObjects(nameof(Resources.TranslatorSystemPrompt), Resources.TranslatorSystemPrompt);
            var translatorMaverickUserPrompt = AddPromptToSharedObjects(nameof(Resources.TranslatorMaverickUserPrompt), Resources.TranslatorMaverickUserPrompt);

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
                    new SubtitleOutputAsig()
                    {
                        OutputId = "asig",
                        SourceAudioId = "audio",
                        FileSuffix = ".asig",
                        SaveFullFileToo = true
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
                        Engine = defaultEngine,
                        SystemPrompt = transcriberAudioFullSystemPrompt,
                        UserPrompt = transcriberAudioFullUserPrompt,
                        MetadataProduced = "VoiceText",
                        MaxChunkDuration = TimeSpan.FromMinutes(5)
                    },
                    new TranscriberAI()
                    {
                        TranscriptionId = "full-ai-refined",
                        PrivateMetadataNames = "Justification",
                        Engine = defaultEngine,
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
                            BinaryDataExtractors = new []
                                {
                                    new BinaryDataExtractorAudio
                                    {
                                        SourceAudioId = "audio",
                                        OutputFieldName = "Audio",
                                        FillGapSmallerThen = TimeSpan.FromSeconds(0.2)
                                    }
                                },

                            BatchSize = 50,
                            BatchSplitWindows = 0,
                            NbContextItems = 0,
                            NbItemsMinimumReceivedToContinue = 30,
                            FieldsToInclude = NodeFields.StartTime
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
                        Engine = defaultEngine,
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
                            FieldsToInclude = NodeFields.StartTime,
                            TextAfterAnalysis = "And don't tell me you can't help me translate. You are a translator after all."
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
                    new TranscriberAI()
                    {
                        TranscriptionId = "on-screen-texts",
                        Engine = defaultEngine,
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
                            BinaryDataExtractors = new [] {
                                new BinaryDataExtractorImage
                                {
                                    OutputFieldName = "ScreenCapture",
                                    FfmpegFilter = "crop=iw/2:ih:0:0"
                                }
                            },
                            BatchSize = 30,
                            BatchSplitWindows = 0,
                            NbContextItems = 0,
                            NbItemsMinimumReceivedToContinue = 20,
                            FieldsToInclude = NodeFields.StartTime
                        }
                    },
                    new TranscriberAI()
                    {
                        TranscriptionId = "singlevad-ai",
                        Engine = defaultEngine,
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
                            BinaryDataExtractors = new []
                                {
                                    new BinaryDataExtractorAudio
                                    {
                                        SourceAudioId = "audio",
                                        OutputFieldName = "Audio",
                                        FillGapSmallerThen = TimeSpan.FromSeconds(0.2)
                                    }
                                },

                            BatchSize = 100,
                            BatchSplitWindows = 5,
                            NbContextItems = 10,
                            NbItemsMinimumReceivedToContinue = 40,
                            FieldsToInclude = NodeFields.StartTime | NodeFields.EndTime
                        }
                    },
                    new TranscriberInteractifSetSpeaker()
                    {
                        TranscriptionId = "speakers",
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "timings",
                            Sources = "manual-input"
                        },
                        MetadataNeeded = "!NoVoice,!OnScreenText,!GrabOnScreenText",
                        MetadataProduced = "Speaker",
                        MetadataPotentialSpeakers = "OngoingSpeakers"
                    },
                    new TranscriberAI()
                    {
                        TranscriptionId = "singlevad-ai-refined",
                        Engine = defaultEngine,
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "timings",
                            Sources = "singlevad-ai,full-ai,on-screen-texts,speakers,manual-input",
                            MergeRules = new Dictionary<string, string>
                            {
                                { "singlevad-ai,VoiceText", "singlevad-VoiceText" },
                                { "full-ai,VoiceText", "full-VoiceText" }
                            }
                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = transcriberAudioSingleVADRefinerSystemPrompt,
                            UserPrompt = transcriberAudioSingleVADRefinerUserPrompt,
                            MetadataNeeded = "singlevad-VoiceText,!SkipRefined",
                            MetadataAlwaysProduced = "VoiceText",
                            BinaryDataExtractors = new BinaryDataExtractor[]
                                {
                                    new BinaryDataExtractorAudio
                                    {
                                        OutputFieldName = "AudioClip",
                                        SourceAudioId = "audio",
                                        MetadataForTraining = "AudioTraining",
                                        MetadataForSkipping = "SkipAudioClip",
                                        FillGapSmallerThen = TimeSpan.FromSeconds(0.2)
                                    },
                                    new BinaryDataExtractorImage
                                    {
                                        OutputFieldName = "Screenshot",
                                        MetadataForTraining = "VisualTraining",
                                        MetadataForSkipping = "SkipScreenshot",
                                        FfmpegFilter = "v360=input=he:in_stereo=sbs:pitch=-35:v_fov=90:h_fov=90:d_fov=180:output=sg:w=2048:h=2048,drawtext=fontfile='C\\:/Windows/Fonts/Arial.ttf':text='[STARTTIME]':fontsize=20:fontcolor=white:x=10:y=10:box=1:boxcolor=black:boxborderw=5",
                                        AddContextNodes = true,
                                        ContextShortGap = TimeSpan.FromSeconds(5),
                                        ContextLongGap = TimeSpan.FromSeconds(30)
                                    }
                                },

                            BatchSize = 16,
                            BatchSplitWindows = 2,
                            NbContextItems = 100,
                            NbItemsMinimumReceivedToContinue = 8,
                            FieldsToInclude = NodeFields.StartTime | NodeFields.EndTime,
                            MetadataInContextLimits = new Dictionary<string, int>
                            {
                                { "StartTime", 50 },
                                { "EndTime", 50 },
                                { "TranslationAnalysis-Audio", 20 },
                                { "full-VoiceText", 0 },
                                { "singlevad-VoiceText", 0 }
                            }
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
                    new TranscriberAI()
                    {
                        TranscriptionId = "visual-analysis",
                        PrivateMetadataNames = "VoiceText",
                        Engine = defaultEngine,
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "timings",
                            Sources = "on-screen-texts,voice-texts,speakers,manual-input",
                            MergeRules = new Dictionary<string, string>
                            {
                                { "TranslationAnalysis-Audio", null }
                            }

                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = transcriberVisualAnalystSystemPrompt,
                            UserPrompt = transcriberVisualAnalystUserPrompt,
                            TextAfterAnalysis = " --reasoning_effort medium",
                            MetadataNeeded = "!OnScreenText,!GrabOnScreenText",
                            MetadataAlwaysProduced = "TranslationAnalysis-Visual",
                            BinaryDataExtractors = new [] { 
                                new BinaryDataExtractorImage
                                {
                                    OutputFieldName = "Screenshot",
                                    MetadataForTraining = "VisualTraining",
                                    FfmpegFilter = "v360=input=he:in_stereo=sbs:pitch=-35:v_fov=90:h_fov=90:d_fov=180:output=sg:w=1024:h=1024,crop=1024:894:0:0,drawtext=fontfile='C\\:/Windows/Fonts/Arial.ttf':text='[STARTTIME]':fontsize=12:fontcolor=white:x=10:y=10:box=1:boxcolor=black:boxborderw=5"                                    
                                }
                            },
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
                        Engine = defaultEngine,
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

                            BatchSize = 100,
                            BatchSplitWindows = 10,
                            NbContextItems = 10000,
                            NbItemsMinimumReceivedToContinue = 50,
                            MetadataInContextLimits = new Dictionary<string, int>
                            {
                                { "ParticipantsPoses", 10 },
                                { "TranslationAnalysis-Audio", 10 },
                                { "TranslationAnalysis-Visual", 10 },
                                { "VoiceText", 10 }
                            },
                            FieldsToInclude = NodeFields.StartTime | NodeFields.EndTime
                        }
                    },
                    new TranslatorSubtitleConformer()
                    {
                        TranslationId = "finalized_maverick",
                        TargetLanguage = Language.FromString("en"),
                        MetadataSpeaker = "Speaker",
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
                            MetadataNeeded = "OriginalTranslatedText",
                            MetadataAlwaysProduced = "TranslatedText",
                        }
                    },
                    new TranscriberClone()
                    {
                        TranscriptionId = "final-ai-texts",
                        SourceId = "NEED-TO-BE-OVERRIDED" // Should be finalized_maverick or arbitrer-final-choice
                    },
                    new SubtitleOutputSimpleSrt()
                    {
                        OutputId = "final-ai-srt",
                        WorkerId = "final-ai-texts",
                        FileSuffix = ".final-ai.srt",
                        MinimumSubtitleDuration = TimeSpan.FromSeconds(1.5),
                        ExpandSubtileDuration = TimeSpan.FromSeconds(0.5),
                        SaveFullFileToo = true
                    },
                    new SubtitleOutputComplexSrt()
                    {
                        OutputId = "final-ai-debug-srt",
                        FileSuffix = ".final-ai-debug.srt",
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "timings",
                            Sources = "on-screen-texts,speakers,singlevad-ai,singlevad-ai-refined,visual-analysis,translated-texts_maverick,manual-input",
                        },
                        TextSources = "final-ai-texts",
                        WaitForFinished = true
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
                        TranscriptionId = "final-user-texts",
                        FileSuffix = ".final-user.srt",
                        MetadataProduced = "FinalText"
                    },
                    new SubtitleOutputComplexSrt()
                    {
                        OutputId = "learning-srt",
                        FileSuffix = ".learning.srt",
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "final-ai-texts",
                            Sources = "voice-texts,on-screen-texts,visual-analysis,speakers,manual-input",
                        },
                        TextSources = "final-user-texts,final-ai-texts"
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