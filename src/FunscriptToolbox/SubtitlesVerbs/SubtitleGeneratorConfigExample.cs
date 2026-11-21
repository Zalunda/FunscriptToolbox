using FunscriptToolbox.Properties;
using FunscriptToolbox.SubtitlesVerbs.AudioExtractions;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using FunscriptToolbox.SubtitlesVerbs.Outputs;
using FunscriptToolbox.SubtitlesVerbs.Transcriptions;
using FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner;
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

            var transcriberAudioSingleVADSystemPrompt = AddPromptToSharedObjects(nameof(Resources.TranscriberAudioSingleVADSystemPrompt), Resources.TranscriberAudioSingleVADSystemPrompt);
            var transcriberAudioSingleVADUserPrompt = AddPromptToSharedObjects(nameof(Resources.TranscriberAudioSingleVADUserPrompt), Resources.TranscriberAudioSingleVADUserPrompt);

            var transcriberAudioSingleVADRefinerSystemPrompt = AddPromptToSharedObjects(nameof(Resources.TranscriberAudioSingleVADRefinerSystemPrompt), Resources.TranscriberAudioSingleVADRefinerSystemPrompt);
            var transcriberAudioSingleVADRefinerUserPrompt = AddPromptToSharedObjects(nameof(Resources.TranscriberAudioSingleVADRefinerUserPrompt), Resources.TranscriberAudioSingleVADRefinerUserPrompt);

            var transcriberOnScreenTextSystemPrompt = AddPromptToSharedObjects(nameof(Resources.TranscriberOnScreenTextSystemPrompt), Resources.TranscriberOnScreenTextSystemPrompt);

            var translatorSystemPrompt = AddPromptToSharedObjects(nameof(Resources.TranslatorSystemPrompt), Resources.TranslatorSystemPrompt);
            var translatorMaverickUserPrompt = AddPromptToSharedObjects(nameof(Resources.TranslatorMaverickUserPrompt), Resources.TranslatorMaverickUserPrompt);

            // The prompts of the timing refiner (manual-input-preparer)
            var timingStartJudgeSystemPrompt = AddPromptToSharedObjects(nameof(Resources.TimingStartJudgeSystemPrompt), Resources.TimingStartJudgeSystemPrompt);
            var timingEndJudgeSystemPrompt = AddPromptToSharedObjects(nameof(Resources.TimingEndJudgeSystemPrompt), Resources.TimingEndJudgeSystemPrompt);
            var timingEndTailSystemPrompt = AddPromptToSharedObjects(nameof(Resources.TimingEndTailSystemPrompt), Resources.TimingEndTailSystemPrompt);
            var timingVerifySystemPrompt = AddPromptToSharedObjects(nameof(Resources.TimingVerifySystemPrompt), Resources.TimingVerifySystemPrompt);

            // A request of the timing refiner: one audio clip per node, the node known only by its StartTime
            static AIOptions CreateTimingInnerOptions(AIPrompt systemPrompt, string metadataProduced) => new AIOptions()
            {
                SystemPrompt = systemPrompt,
                MetadataNeeded = "",
                MetadataAlwaysProduced = metadataProduced,
                BinaryDataExtractors = new BinaryDataExtractor[]
                {
                    new BinaryDataExtractorAudio()
                    {
                        OutputFieldName = "AudioClip",
                        Enabled = true,
                        ExpandStart = TimeSpan.Zero,
                        ExpandEnd = TimeSpan.Zero,
                        KeepTemporaryFiles = false,
                        SourceAudioId = "audio"
                    }
                },
                MaximumRequestsInBatch = 30,
                BatchSize = 25,
                BatchSplitWindows = 0,
                BatchKeepTogetherThreshold = TimeSpan.Zero,
                NbContextItems = 0,
                NbItemsMinimumReceivedToContinue = 15,
                FieldsToInclude = NodeFields.StartTime
            };

            var config = new SubtitleGeneratorConfig()
            {
                SharedObjects = sharedObjects.ToArray(),
                Workers = new SubtitleWorker[]
                {
                    new SubtitleOutputUsedConfig()
                    {
                        OutputId = "used-config",
                        CanBeUpdated = true,
                    },
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
                    // The light video of each part ("<video>.FAST.mp4"), for the Timing editor of the web UI and the speaker correction.
                    // 'crop=iw/2:ih:0:0': the left eye of a VR video (side by side).
                    new SubtitleOutputFastVideo()
                    {
                        OutputId = "fast-video",
                        FileSuffix = ".FAST.mp4",
                        FfmpegParameters = "-vf \"crop=iw/2:ih:0:0,scale=-2:512\" -af \"highpass=f=300,lowpass=f=3500,loudnorm=I=-16:TP=-1\" -ar 48000 -c:v libx264 -g 1 -c:a aac -b:a 128k -movflags +faststart"
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
                    new TranslatorGoogleV1API()
                    {
                        TranslationId = "full-whisper_google",
                        TranscriptionId = "full-whisper",
                        TargetLanguage = Language.FromString("en"),
                        MetadataNeeded = "VoiceText",
                        MetadataProduced = "TranslatedText"
                    },
                    new TranslatorAI()
                    {
                        TranslationId = "full-whisper_local-api",
                        TargetLanguage = Language.FromString("en"),
                        Engine = defaultEngine,
                        Metadatas = new MetadataAggregator
                        {
                            TimingsSource = "full-whisper",
                            Sources = "full-whisper"
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
                    new SubtitleOutputSrt()
                    {
                        OutputId = "preliminary-srt",
                        FileSuffix = ".srt",
                        TextSources = "full-whisper_google"
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
                    new TranslatorGoogleV1API()
                    {
                        TranslationId = "full-ai_google",
                        TranscriptionId = "full-ai",
                        TargetLanguage = Language.FromString("en"),
                        MetadataNeeded = "VoiceText",
                        MetadataProduced = "TranslatedText"
                    },
                    // Prepares the subtitles of 'full-ai' for the manual-input file: refines their timings (terrain of the audio, + AI depending
                    // on RefineTiming.Mode), adds the title cards, and flags what needs a look ('[REVIEW:Code:Detail] reason' lines).
                    // Enabled by the workflows (with 'full' using it as its source).
                    new TranscriberAIManualInputPreparer()
                    {
                        TranscriptionId = "manual-input-preparer",
                        Engine = defaultEngine,
                        PrivateMetadataNames = "TimingTrace",
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "full-ai",
                            Sources = "full-ai"
                        },
                        Options = new AIOptions()
                        {
                            MetadataNeeded = "VoiceText",
                            MetadataAlwaysProduced = "VoiceTextForManualReview"
                        },
                        TitleCardDetection = new TitleCardDetectorOptions()
                        {
                            Enabled = true,
                            FfmpegFilter = "crop=iw*0.20:ih*0.10:iw*0.15:ih*0.45"
                        },
                        RefineTiming = new TimingRefinerSettings()
                        {
                            // Off / SnapOnly (no AI request) / SnapAndVerify (one AI request per subtitle) / Full (the AI picks the start and the end)
                            Mode = RefineMode.SnapOnly,
                            SourceAudioIdForTerrain = "audio-clean-waveform",
                            MaxTestsInRequest = 5,
                            MaxDipsInward = 2,
                            MaxDipsOutward = 2,
                            StartPassTailPadding = TimeSpan.FromSeconds(0.8),
                            MaxExpansion = TimeSpan.FromSeconds(2.0),
                            BleedDetectionMargin = TimeSpan.FromSeconds(3.0),
                            TrimSilenceOnStart = true,
                            TrimSilenceOnEnd = true,
                            StartClipPadding = TimeSpan.FromMilliseconds(300),
                            EndClipPadding = TimeSpan.FromMilliseconds(300),
                            InnerOptionsStart = CreateTimingInnerOptions(timingStartJudgeSystemPrompt, "BestStart"),
                            InnerOptionsEnd = CreateTimingInnerOptions(timingEndJudgeSystemPrompt, "BestEnd"),
                            EndTailKeepIfSpeechRatio = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
                            {
                                { "Silence", 0.3f },
                                { "Moaning", 0.5f }
                            },
                            EndTailMinExtra = TimeSpan.FromSeconds(0.4),
                            EndTailMaxChecks = 1,
                            InnerOptionsEndTail = CreateTimingInnerOptions(timingEndTailSystemPrompt, "TailKind"),
                            InnerOptionsVerify = CreateTimingInnerOptions(timingVerifySystemPrompt, "Verdict")
                        },
                        Validation = new TimingValidatorSettings()
                        {
                            Enabled = true,
                            // Warn / Ignore / Fix (LongWithSilence only)
                            Validations = new Dictionary<ValidationCode, ValidationMode>()
                            {
                                { ValidationCode.MissingStart, ValidationMode.Warn },
                                { ValidationCode.MissingEnd, ValidationMode.Warn },
                                { ValidationCode.Unfinished, ValidationMode.Warn },
                                { ValidationCode.TextMismatch, ValidationMode.Warn },
                                { ValidationCode.Inaudible, ValidationMode.Warn },
                                { ValidationCode.Overlap, ValidationMode.Warn },
                                { ValidationCode.BoundariesCrossed, ValidationMode.Warn },
                                { ValidationCode.NoAnswer, ValidationMode.Warn },
                                { ValidationCode.UnrecognisedAnswer, ValidationMode.Warn },
                                { ValidationCode.NoSpeechInside, ValidationMode.Warn },
                                { ValidationCode.TooShort, ValidationMode.Warn },
                                { ValidationCode.TooFast, ValidationMode.Warn },
                                { ValidationCode.TinyGap, ValidationMode.Warn },
                                { ValidationCode.SpeechRateHigh, ValidationMode.Warn },
                                { ValidationCode.SpeechRateLow, ValidationMode.Warn },
                                { ValidationCode.RepeatedText, ValidationMode.Warn },
                                { ValidationCode.TextLoop, ValidationMode.Warn },
                                { ValidationCode.MaybePointless, ValidationMode.Warn },
                                { ValidationCode.LongWithSilence, ValidationMode.Warn },
                                { ValidationCode.SplitPart, ValidationMode.Warn },
                                { ValidationCode.EmptyText, ValidationMode.Warn },
                                { ValidationCode.TitleCard, ValidationMode.Warn }
                            },
                            // (made for Japanese)
                            Checks = new TimingChecksSettings()
                            {
                                MinDuration = TimeSpan.FromMilliseconds(300),
                                MaxCharsPerSecond = 15,
                                MinGap = TimeSpan.FromMilliseconds(50),
                                MaxCharsPerSpeechSecond = 14,
                                MinCharsPerSpeechSecond = 1.0,
                                MaxSubtitleDuration = TimeSpan.FromSeconds(5),
                                MinSilenceToSplit = TimeSpan.FromSeconds(1),
                                SplitStrategy = SplitStrategy.UnderMaxDuration,
                                TextMismatchMinSimilarity = 0.8,
                                RepeatedTextMinSimilarity = 0.9,
                                RepeatedTextMinChars = 8,
                                MaybePointlessPatterns = new[]
                                {
                                    // Moans, grunt, etc
                                    new TranscriberAudioFullAI.TranscriptioToIgnorePattern("A-Moans", @"[あぁーっ、。\s…!！]+"),
                                    new TranscriberAudioFullAI.TranscriptioToIgnorePattern("N-Hums", @"[んっー、。\s…!！]+"),
                                    new TranscriberAudioFullAI.TranscriptioToIgnorePattern("U-Grunts", @"[うぅーっ、。\s…!！]+"),
                                    new TranscriberAudioFullAI.TranscriptioToIgnorePattern("Breathing", @"[はぁーっ、。\s…!！]+"),
                                    new TranscriberAudioFullAI.TranscriptioToIgnorePattern("Giggle", @"ふふ[っ]*[。！？]*"),
                                    new TranscriberAudioFullAI.TranscriptioToIgnorePattern("CombinedFiller", @"[あぁうぅんっーは、。\s…!！]+"),
                                    // Words said over and over
                                    new TranscriberAudioFullAI.TranscriptioToIgnorePattern("Sugoi (すごい = incredible)", @"(すご|スゴ|凄)[いイーっッ]+[、。\s…!！?？]*"),
                                    new TranscriberAudioFullAI.TranscriptioToIgnorePattern("Kimochii (きもちいい = it feels good)", @"(きもち|キモチ|気持ち)[いイーっッ]+[、。\s…!！?？]*"),
                                    new TranscriberAudioFullAI.TranscriptioToIgnorePattern("Ii (いい = good)", @"[いイ][いイー]+[っッ]*[、。\s…!！?？]*"),
                                    new TranscriberAudioFullAI.TranscriptioToIgnorePattern("Iku (いく = I'm coming)", @"[いイ](く|ク|[っッ]く|[っッ]ク|きそう|キソウ|[っッ]ちゃう|[っッ]ちゃいそう)[ーっ]*[、。\s…!！?？]*"),
                                    new TranscriberAudioFullAI.TranscriptioToIgnorePattern("Dame (だめ = no, stop)", @"(だめ|ダメ|駄目)[ーっ]*[、。\s…!！?？]*"),
                                    new TranscriberAudioFullAI.TranscriptioToIgnorePattern("Iya (いや, やだ = no, stop it)", @"(いや|イヤ|やだ|ヤダ|やん)[あんーっ]*[、。\s…!！?？]*"),
                                    new TranscriberAudioFullAI.TranscriptioToIgnorePattern("Yabai (やばい = crazy, damn)", @"(やば|ヤバ)[いイー]+[、。\s…!！?？]*"),
                                    new TranscriberAudioFullAI.TranscriptioToIgnorePattern("Motto (もっと = more)", @"(もっと|モット)[ーっ]*[、。\s…!！?？]*"),
                                    new TranscriberAudioFullAI.TranscriptioToIgnorePattern("Nee (ねえ = hey)", @"[ねネ][えぇエェーっ]+[、。\s…!！?？]*"),
                                    new TranscriberAudioFullAI.TranscriptioToIgnorePattern("Hai (はい = yes)", @"(はい|ハイ)[ーっ]*[、。\s…!！?？]*"),
                                    new TranscriberAudioFullAI.TranscriptioToIgnorePattern("Soko (そこ = right there)", @"(そこ|ソコ)[ーっ]*[、。\s…!！?？]*"),
                                    new TranscriberAudioFullAI.TranscriptioToIgnorePattern("Hora (ほら = look, here)", @"(ほら|ホラ)[ーっ]*[、。\s…!！?？]*")
                                }
                            }
                        }
                    },
                    // (an alias: another name for its source, so that the next steps always use the same name; nothing is copied)
                    new TranscriberAlias()
                    {
                        TranscriptionId = "full",
                        SourceId = "NEED-TO-BE-OVERRIDED" // Should be full-whisper, full-ai or manual-input-preparer
                    },

                    //---------------------------------------------------
                    // finalizing timings, manual-input and voice-texts clone
                    new SubtitleOutputSrt()
                    {
                        OutputId = "generated-manual-input-srt",
                        FileSuffix = ".manual-input.srt",
                        TextSources = "full",
                        AddToFirstSubtitle = "[USER-REVISION-NEEDED] <= remove this line when revision is done\n{OngoingContext:The scene take place in...}\n{OngoingSpeakers:Woman}\nOther metadatas to use in the file:\n- GrabOnScreenText\n- VisualTraining (for visual-analyst)"
                    },
                    // The review of the manual-input file in the Timing editor of the web UI (button 'Timing' of the scene), instead of editing
                    // the file by hand (replaces 'generated-manual-input-srt'). Kept in the .wipsubtitles; when the user clicks 'Review done',
                    // the file is written (then imported by 'manual-input'). ReviewNeeded = false: the file is written right away (no review).
                    new TranscriberInteractifReview()
                    {
                        TranscriptionId = "manual-input-review",
                        FileSuffix = ".manual-input.srt",
                        // (the text of each source: its own metadata, ex. VoiceTextForManualReview for the preparer, with its review warnings.
                        // Several sources, ex. "manual-input-preparer,full-ai_google": each one is under a line with its name)
                        TextSources = "manual-input-preparer",
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "manual-input-preparer",
                            Sources = "manual-input-preparer",
                            MergeRules = new MergeRuleDictionary()
                            {
                                { "VoiceText", null },
                                { "VoiceTextValidated", null },
                                { "VoiceTextForManualReview", null }
                            }
                        },
                        // (the speakers are set with the 'Speakers' pass of the Timing editor)
                        // (the other properties, ex. GrabOnScreenText, VisualTraining: added in the Properties tab of the Timing editor)
                        AddToFirstSubtitle = "{OngoingContext:The scene take place in...}",
                        SpeakerMetadata = "Speaker",
                        OngoingSpeakersMetadata = "OngoingSpeakers"
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
                            Sources = "on-screen-texts,manual-input"
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
                            BatchKeepTogetherThreshold = TimeSpan.FromMilliseconds(30),
                            NbContextItems = 10,
                            NbItemsMinimumReceivedToContinue = 40,
                            FieldsToInclude = NodeFields.StartTime | NodeFields.EndTime
                        }
                    },
                    new TranscriberAI()
                    {
                        TranscriptionId = "singlevad-ai-refined",
                        Engine = defaultEngine,
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "timings",
                            Sources = "singlevad-ai,full-ai,on-screen-texts,manual-input",
                            MergeRules = new MergeRuleDictionary()
                            {
                                { "singlevad-ai,VoiceText", "singlevad-VoiceText" },
                                { "full-ai,VoiceText", "full-VoiceText" }
                            }
                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = transcriberAudioSingleVADRefinerSystemPrompt,
                            UserPrompt = transcriberAudioSingleVADRefinerUserPrompt,
                            MetadataNeeded = "!NoVoice,!OnScreenText,!GrabOnScreenText,!SkipRefined",
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
                            BatchKeepTogetherThreshold = TimeSpan.FromMilliseconds(30),
                            NbContextItems = 100,
                            NbItemsMinimumReceivedToContinue = 8,
                            FieldsToInclude = NodeFields.StartTime | NodeFields.EndTime,
                            MetadataInContextLimits = new Dictionary<string, int>
                            {
                                { "StartTime", 10 },
                                { "EndTime", 10 },
                                { "TranslationAnalysis", 10 },
                                { "full-VoiceText", 0 },
                                { "singlevad-VoiceText", 0 }
                            }
                        }
                    },
                    new TranscriberAlias()
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
                    new SubtitleOutputSrt()
                    {
                        OutputId = "metadatas-srt",
                        FileSuffix = ".metadatas.srt",
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "timings",
                            Sources = "singlevad-ai-refined,voice-texts,on-screen-texts,manual-input",
                        },
                        WaitForFinished = true
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
                            Sources = "singlevad-ai-refined,voice-texts,on-screen-texts,manual-input"
                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = translatorSystemPrompt,
                            UserPrompt = translatorMaverickUserPrompt,

                            MetadataNeeded = "VoiceText|OnScreenText,!SkipMaverick",
                            MetadataAlwaysProduced = "TranslatedText",

                            BatchSize = 100,
                            BatchSplitWindows = 10,
                            NbContextItems = 10000,
                            NbItemsMinimumReceivedToContinue = 50,
                            MetadataInContextLimits = new Dictionary<string, int>
                            {
                                { "StartTime", 50 },
                                { "EndTime", 50 },
                                { "TranslationAnalysis", 10 },
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
                            Sources = "translated-texts_maverick,manual-input",
                            MergeRules = new MergeRuleDictionary()
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
                    new TranscriberAlias()
                    {
                        TranscriptionId = "final-ai-texts",
                        SourceId = "NEED-TO-BE-OVERRIDED" // Should be finalized_maverick
                    },
                    new SubtitleOutputSrt()
                    {
                        OutputId = "final-ai-srt",
                        TextSources = "final-ai-texts",
                        FileSuffix = ".final-ai.srt",
                        MinimumSubtitleDuration = TimeSpan.FromSeconds(1.5),
                        ExpandSubtileDuration = TimeSpan.FromSeconds(0.5),
                        SaveFullFileToo = true
                    },
                    new SubtitleOutputSrt()
                    {
                        OutputId = "final-ai-debug-srt",
                        FileSuffix = ".final-ai-debug.srt",
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "timings",
                            Sources = "on-screen-texts,full-ai,singlevad-ai,singlevad-ai-refined,translated-texts_maverick,manual-input",
                        },
                        TextSources = "",
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
                    new SubtitleOutputSrt()
                    {
                        OutputId = "learning-srt",
                        FileSuffix = ".learning.srt",
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "final-ai-texts",
                            Sources = "singlevad-ai-refined,voice-texts,on-screen-texts,manual-input",
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