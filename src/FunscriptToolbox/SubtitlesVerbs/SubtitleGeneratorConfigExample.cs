using FunscriptToolbox.Properties;
using FunscriptToolbox.SubtitlesVerbs.Inputs;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using FunscriptToolbox.SubtitlesVerbs.Outputs;
using FunscriptToolbox.SubtitlesVerbs.Processors;
using FunscriptToolbox.SubtitlesVerbs.Processors.Refiner;
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
            var defaultEngine = AddSharedObject("AIEngineNeedToBeOverriden", new AIEngineNeedToBeOverriden());

            // The transcription model of Google (another API than the other Gemini models, no batch mode): see TranscriberAudioFullAIGoogleTranscribe.
            var googleTranscribeEngine = AddSharedObject("AIEngineGeminiTranscribeOnGoogle", new AIEngineAPIGoogle
            {
                BaseAddress = "https://generativelanguage.googleapis.com/v1beta",
                Model = "gemini-3.5-transcribe",
                APIKeyName = "APIKeyGemini",
                UseBatchMode = false,
                SupportAudio = true,
                EstimatedCostPerInputMillionTokens = 2,
                EstimatedCostPerOutputMillionTokens = 12,
                TranscribeMinTimeBetweenRequests = TimeSpan.FromSeconds(30),
                TranscribeMaxRetriesOnTooManyRequests = 5
            });

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
            var timingSpokenMarkersSystemPrompt = AddPromptToSharedObjects(nameof(Resources.TimingSpokenMarkersSystemPrompt), Resources.TimingSpokenMarkersSystemPrompt);

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

            // The engines of the timing refiner (manual-input-preparer: RefineTiming.Engine), from the cheapest to the most expensive in AI tokens
            var timingRefineEngineNone = AddSharedObject(nameof(TimingRefineEngineNone), new TimingRefineEngineNone());
            AddSharedObject(nameof(TimingRefineEngineVerify), new TimingRefineEngineVerify
            {
                InnerOptions = CreateTimingInnerOptions(timingVerifySystemPrompt, "Verdict")
            });
            AddSharedObject(nameof(TimingRefineEngineStartEndJudge), new TimingRefineEngineStartEndJudge
            {
                InnerOptionsStart = CreateTimingInnerOptions(timingStartJudgeSystemPrompt, "BestStart"),
                InnerOptionsEnd = CreateTimingInnerOptions(timingEndJudgeSystemPrompt, "BestEnd"),
                MaxTestsInRequest = 5,
                MaxDipsInward = 2,
                MaxDipsOutward = 2,
                MaxExpansion = TimeSpan.FromSeconds(2.0),
                StartPassTailPadding = TimeSpan.FromSeconds(0.8),
                StartClipPadding = TimeSpan.FromMilliseconds(300),
                EndClipPadding = TimeSpan.FromMilliseconds(300),
                EndTail = new TimingRefineEndTailSettings
                {
                    InnerOptions = CreateTimingInnerOptions(timingEndTailSystemPrompt, "TailKind"),
                    MinExtra = TimeSpan.FromSeconds(0.4),
                    MaxChecks = 1,
                    KeepIfSpeechRatio = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
                    {
                        { "Silence", 0.3f },
                        { "Moaning", 0.5f }
                    }
                }
            });
            // The spoken markers: ONE request per subtitle and per run, in batches of 50 nodes (the batch of the tests: the less the AI
            // thinks per node, the cheaper; the AI engine needs a maxOutputTokens big enough for 50 answers, ex. 16384)
            var spokenMarkersOptions = CreateTimingInnerOptions(timingSpokenMarkersSystemPrompt, TimingMeta.MarkedTranscription);
            spokenMarkersOptions.BatchSize = 50;
            spokenMarkersOptions.NbItemsMinimumReceivedToContinue = 40;
            AddSharedObject(nameof(TimingRefineEngineSpokenMarkers), new TimingRefineEngineSpokenMarkers
            {
                InnerOptions = spokenMarkersOptions,
                Runs = 2,
                OnDisagreement = SpokenMarkersPick.Wider,
                StartOptionsBefore = 2,
                StartOptionsAfter = 2,
                EndOptionsBefore = 4,
                EndOptionsAfter = 2,
                MaxExpansion = TimeSpan.FromSeconds(2.0),
                MarkerMergeDistance = TimeSpan.FromMilliseconds(80),
                ClipMargin = TimeSpan.FromSeconds(0.7)
            });

            var config = new SubtitleGeneratorConfig()
            {
                SharedObjects = sharedObjects.ToArray(),
                Workers = new SubtitleWorker[]
                {
                    new OutputUsedConfig()
                    {
                        Id = "used-config",
                        CanBeUpdated = true,
                    },
                    //---------------------------------------------------
                    // Audio extraction
                    new AudioExtractorFromVideo()
                    {
                        Id = "audio"
                    },
                    new AudioExtractorFromPcm()
                    {
                        Id = "audio-clean-waveform",
                        SourceAudioId = "audio",
                        SaveAsFileSuffixe = ".wav",
                        FfmpegParameters = "-af \"highpass=f=300,lowpass=f=3500,loudnorm=I=-16:TP=-1\"" // <lowpass>,anlmdn,agate=threshold=0.04,<loudnorm>
                    },
                    new OutputAsig()
                    {
                        Id = "asig",
                        SourceAudioId = "audio",
                        FileSuffix = ".asig",
                        SaveFullFileToo = true
                    },
                    // The light video of each part ("<video>.FAST.mp4"), for the Review editor of the web UI and the speaker correction.
                    // 'crop=iw/2:ih:0:0': the left eye of a VR video (side by side).
                    new OutputFastVideo()
                    {
                        Id = "fast-video",
                        FileSuffix = ".FAST.mp4",
                        FfmpegParameters = "-vf \"crop=iw/2:ih:0:0,scale=-2:512\" -af \"highpass=f=300,lowpass=f=3500,loudnorm=I=-16:TP=-1\" -ar 48000 -c:v libx264 -g 1 -c:a aac -b:a 128k -movflags +faststart"
                    },

                    //---------------------------------------------------
                    // Full transcription (whisper or AI)
                    new TranscriberAudioFull()
                    {
                        Id = "full-whisper",
                        SourceAudioId = "audio",
                        MetadataProduced = "VoiceText",
                        TranscriberTool = transcriberToolPurfviewWhisper,
                    },
                    new TransformGoogleTranslate()
                    {
                        Id = "full-whisper_google",
                        SourceId = "full-whisper",
                        Produces = "Translation",
                        Language = Language.FromString("en"),
                        MetadataNeeded = "VoiceText",
                        MetadataProduced = "TranslatedText"
                    },
                    new TransformAI()
                    {
                        Id = "full-whisper_local-api",
                        Produces = "Translation",
                        Language = Language.FromString("en"),
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
                            NbItemsMinimumReceivedToContinue = 1,
                            FieldsToInclude = NodeFields.StartTime,
                            TextAfterAnalysis = "And don't tell me you can't help me translate. You are a translator after all."
                        }
                    },
                    new OutputSrt()
                    {
                        Id = "preliminary-srt",
                        FileSuffix = ".srt",
                        TextSources = "full-whisper_google"
                    },
                    new TranscriberAudioFullAI()
                    {
                        Id = "full-ai",
                        SourceAudioId = "audio",
                        Engine = defaultEngine,
                        SystemPrompt = transcriberAudioFullSystemPrompt,
                        UserPrompt = transcriberAudioFullUserPrompt,
                        MetadataProduced = "VoiceText",
                        MaxChunkDuration = TimeSpan.FromMinutes(5)
                    },
                    // Full transcription by the transcription model of Google: verbatim, with the start / end of each word (good timings);
                    // the subtitles are built from the words. Not used for now (disabled; being tested): a workflow can enable it.
                    new TranscriberAudioFullAIGoogleTranscribe()
                    {
                        Id = "full-transcribe",
                        SourceAudioId = "audio",
                        Engine = googleTranscribeEngine,
                        MetadataProduced = "VoiceText",
                        MaxChunkDuration = TimeSpan.FromMinutes(25),
                        Diarization = false,
                        SpeakerMetadata = null,
                        MultipleSpeakersMetadata = "Speaker",
                        MultipleSpeakersValue = "Multiple",
                        SameLineMaxStartDifference = TimeSpan.FromSeconds(1.5),
                        MaxGap = TimeSpan.FromMilliseconds(600),
                        MaxDuration = TimeSpan.FromSeconds(6),
                        EndPadding = TimeSpan.FromMilliseconds(250)
                    },
                    new TransformGoogleTranslate()
                    {
                        Id = "full-ai_google",
                        SourceId = "full-ai",
                        Produces = "Translation",
                        Language = Language.FromString("en"),
                        MetadataNeeded = "VoiceText",
                        MetadataProduced = "TranslatedText"
                    },
                    // Prepares the subtitles of 'full-ai' for the manual-input file: refines their timings (terrain of the audio, + AI depending
                    // on RefineTiming.Engine), adds the title cards, and flags what needs a look ('[REVIEW:Code:Detail] reason' lines).
                    // Enabled by the workflows (with 'full' using it as its source).
                    new TransformManualInputPreparer()
                    {
                        Id = "manual-input-preparer",
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
                            Enabled = true,
                            SourceAudioIdForTerrain = "audio-clean-waveform",
                            BleedDetectionMargin = TimeSpan.FromSeconds(3.0),
                            TrimSilenceOnStart = true,
                            TrimSilenceOnEnd = true,
                            // TimingRefineEngineNone (no AI request) / TimingRefineEngineVerify (one AI request per subtitle) /
                            // TimingRefineEngineStartEndJudge (the AI picks the start and the end): see the shared objects
                            Engine = timingRefineEngineNone
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
                                { ValidationCode.InsideNeighbour, ValidationMode.Warn },
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
                                    // Moans, grunt, etc (ignored in the name of a combination: "ああ、きもちいい" is "Kimochii")
                                    new TimingChecksSettings.MaybePointlessPattern("A-Moans", @"[あぁーっ、。\s…!！]+", ignoredInCombination: true),
                                    new TimingChecksSettings.MaybePointlessPattern("N-Hums", @"[んっー、。\s…!！]+", ignoredInCombination: true),
                                    new TimingChecksSettings.MaybePointlessPattern("U-Grunts", @"[うぅーっ、。\s…!！]+", ignoredInCombination: true),
                                    new TimingChecksSettings.MaybePointlessPattern("Breathing", @"[はぁーっ、。\s…!！]+", ignoredInCombination: true),
                                    new TimingChecksSettings.MaybePointlessPattern("Giggle", @"ふふ[っ]*[。！？]*", ignoredInCombination: true),
                                    new TimingChecksSettings.MaybePointlessPattern("CombinedFiller", @"[あぁうぅんっーは、。\s…!！]+", ignoredInCombination: true),
                                    // Words said over and over
                                    new TimingChecksSettings.MaybePointlessPattern("Sugoi (すごい = incredible)", @"(すご|スゴ|凄)[いイーっッ]+[、。\s…!！?？]*"),
                                    new TimingChecksSettings.MaybePointlessPattern("Kimochii (きもちいい = it feels good)", @"(きもち|キモチ|気持ち)[いイーっッ]+[、。\s…!！?？]*"),
                                    new TimingChecksSettings.MaybePointlessPattern("Ii (いい = good)", @"[いイ][いイー]+[っッ]*[、。\s…!！?？]*"),
                                    new TimingChecksSettings.MaybePointlessPattern("Iku (いく = I'm coming)", @"[いイ](く|ク|[っッ]く|[っッ]ク|きそう|キソウ|[っッ]ちゃう|[っッ]ちゃいそう)[ーっ]*[、。\s…!！?？]*"),
                                    new TimingChecksSettings.MaybePointlessPattern("Dame (だめ = no, stop)", @"(だめ|ダメ|駄目)[ーっ]*[、。\s…!！?？]*"),
                                    new TimingChecksSettings.MaybePointlessPattern("Iya (いや, やだ = no, stop it)", @"(いや|イヤ|やだ|ヤダ|やん)[あんーっ]*[、。\s…!！?？]*"),
                                    new TimingChecksSettings.MaybePointlessPattern("Yabai (やばい = crazy, damn)", @"(やば|ヤバ)[いイー]+[、。\s…!！?？]*"),
                                    new TimingChecksSettings.MaybePointlessPattern("Motto (もっと = more)", @"(もっと|モット)[ーっ]*[、。\s…!！?？]*"),
                                    new TimingChecksSettings.MaybePointlessPattern("Nee (ねえ = hey)", @"[ねネ][えぇエェーっ]+[、。\s…!！?？]*"),
                                    new TimingChecksSettings.MaybePointlessPattern("Hai (はい = yes)", @"(はい|ハイ)[ーっ]*[、。\s…!！?？]*"),
                                    new TimingChecksSettings.MaybePointlessPattern("Soko (そこ = right there)", @"(そこ|ソコ)[ーっ]*[、。\s…!！?？]*"),
                                    new TimingChecksSettings.MaybePointlessPattern("Hora (ほら = look, here)", @"(ほら|ホラ)[ーっ]*[、。\s…!！?？]*")
                                }
                            }
                        }
                    },
                    // (an alias: another name for its source, so that the next steps always use the same name; nothing is copied)
                    new TrackAlias()
                    {
                        Id = "full",
                        SourceId = "NEED-TO-BE-OVERRIDED" // Should be full-whisper, full-ai or manual-input-preparer
                    },

                    //---------------------------------------------------
                    // finalizing timings, manual-input and voice-texts clone
                    new OutputSrt()
                    {
                        Id = "generated-manual-input-srt",
                        FileSuffix = ".manual-input.srt",
                        TextSources = "full",
                        AddToFirstSubtitle = "[USER-REVISION-NEEDED] <= remove this line when revision is done\n{OngoingContext:The scene take place in...}\n{OngoingSpeakers:Woman}\nOther metadatas to use in the file:\n- GrabOnScreenText\n- VisualTraining (for visual-analyst)"
                    },
                    // The review of the manual-input file in the Review editor of the web UI (button 'Review' of the scene), instead of editing
                    // the file by hand (replaces 'generated-manual-input-srt'). Kept in the .wipsubtitles; when the user clicks 'Review done',
                    // the file is written (then imported by 'manual-input'). ReviewNeeded = false: the file is written right away (no review).
                    new TransformReview()
                    {
                        Id = "manual-input-review",
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
                        // (the speakers are set with the 'Speakers' pass of the Review editor)
                        // (the other properties, ex. GrabOnScreenText, VisualTraining: added in the Properties tab of the Review editor)
                        AddToFirstSubtitle = "{OngoingContext:The scene take place in...}",
                        SpeakerMetadata = "Speaker",
                        OngoingSpeakersMetadata = "OngoingSpeakers"
                    },
                    new InputImportMetadatas()
                    {
                        Id = "manual-input",
                        FileSuffix = ".manual-input.srt",
                        CanBeUpdated = true,
                        ProcessOnlyWhenStringIsRemoved = "[USER-REVISION-NEEDED]",
                    },
                    new TransformClone()
                    {
                        Id = "timings",
                        SourceId = "manual-input"
                    },
                    new TransformAI()
                    {
                        Id = "on-screen-texts",
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
                    new TransformAI()
                    {
                        Id = "singlevad-ai",
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
                    new TransformAI()
                    {
                        Id = "singlevad-ai-refined",
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
                    new TrackAlias()
                    {
                        Id = "voice-texts",
                        SourceId = "NEED-TO-BE-OVERRIDED" // Should be full-ai or singlevad-ai or singlevad-ai-refined
                    },

                    //---------------------------------------------------
                    // Use timings, manual-input, voice-texts and AI-generated metadatas to the translations and final arbitration
                    new TransformAI()
                    {
                        Id = "translated-texts_maverick",
                        Produces = "Translation",
                        Language = Language.FromString("en"),
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
                    new TransformSubtitleConformer()
                    {
                        Id = "finalized_maverick",
                        Produces = "Translation",
                        Language = Language.FromString("en"),
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
                    new TrackAlias()
                    {
                        Id = "final-ai-texts",
                        SourceId = "NEED-TO-BE-OVERRIDED" // Should be finalized_maverick
                    },
                    new OutputSrt()
                    {
                        Id = "final-ai-srt",
                        TextSources = "final-ai-texts",
                        FileSuffix = ".final-ai.srt",
                        ExpandSubtileDuration = TimeSpan.FromSeconds(0.5),
                        MinimumSubtitleDuration = TimeSpan.FromSeconds(1.5),
                        SaveFullFileToo = true
                    },
                    new OutputSrt()
                    {
                        Id = "final-ai-debug-srt",
                        FileSuffix = ".final-ai-debug.srt",
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "timings",
                            Sources = "on-screen-texts,full-ai,singlevad-ai,singlevad-ai-refined,translated-texts_maverick,manual-input",
                        },
                        TextSources = "",
                        WaitForFinished = true
                    },
                    new OutputCostReport()
                    {
                        Id = "costs",
                        FileSuffix = ".cost.txt",
                        OutputToConsole = false,
                        CanBeUpdated = true
                    },

                    //---------------------------------------------------
                    // Potential 'Learning' workflow
                    new InputImportText
                    {
                        Id = "final-user-texts",
                        FileSuffix = ".final-user.srt",
                        MetadataProduced = "FinalText"
                    },
                    new OutputSrt()
                    {
                        Id = "learning-srt",
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