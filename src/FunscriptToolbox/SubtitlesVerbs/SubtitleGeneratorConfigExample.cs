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

            var transcriberAudioFullSystemPromptForG4 = AddPromptToSharedObjects(nameof(Resources.TranscriberAudioFullSystemPromptForG4), Resources.TranscriberAudioFullSystemPromptForG4);
            var transcriberAudioFullUserPrompt = AddPromptToSharedObjects(nameof(Resources.TranscriberAudioFullUserPrompt), Resources.TranscriberAudioFullUserPrompt);

            var transcriberAudioSingleVADRefinerSystemPromptForG4 = AddPromptToSharedObjects(nameof(Resources.TranscriberAudioSingleVADRefinerSystemPromptForG4), Resources.TranscriberAudioSingleVADRefinerSystemPromptForG4);
            var transcriberAudioSingleVADRefinerUserPromptForG4 = AddPromptToSharedObjects(nameof(Resources.TranscriberAudioSingleVADRefinerUserPromptForG4), Resources.TranscriberAudioSingleVADRefinerUserPromptForG4);

            // (the prompts of the older workflows: also defined in their override, so they can be removed from here in a next version)
            AddPromptToSharedObjects(nameof(Resources.TranscriberAudioFullSystemPrompt), Resources.TranscriberAudioFullSystemPrompt);
            AddPromptToSharedObjects(nameof(Resources.TranscriberAudioSingleVADRefinerSystemPrompt), Resources.TranscriberAudioSingleVADRefinerSystemPrompt);
            AddPromptToSharedObjects(nameof(Resources.TranscriberAudioSingleVADRefinerUserPrompt), Resources.TranscriberAudioSingleVADRefinerUserPrompt);
            AddPromptToSharedObjects(nameof(Resources.TranscriberOnScreenTextSystemPrompt), Resources.TranscriberOnScreenTextSystemPrompt);
            AddPromptToSharedObjects(nameof(Resources.TranslatorMaverickUserPrompt), Resources.TranslatorMaverickUserPrompt);

            var translatorSystemPrompt = AddPromptToSharedObjects(nameof(Resources.TranslatorSystemPrompt), Resources.TranslatorSystemPrompt);
            // (a translation to any language, [TranslationLanguage]: ex. the French example in a workflow override)
            AddPromptToSharedObjects(nameof(Resources.TranslatorUniversalSystemPrompt), Resources.TranslatorUniversalSystemPrompt);

            // The prompts of the timing refiner (annotated-timings-proposal)
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

            // The engines of the timing refiner (annotated-timings-proposal: RefineTiming.Engine), from the cheapest to the most expensive in AI tokens
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
                GlobalShared = new SubtitleGeneratorGlobalSharedSettings
                {
                    MaxConcurrent = new Dictionary<string, int> { { "GPU", 1 }, { "VID", 1 }, { "AUD", 3 }, { "PREP", 1 }, { "ENC", 1 } },
                    ScenePriorities = new[]
                    {
                        new ScenePriorityRule { Metadata = "Type", Pattern = "ManualHQWorkflow-G4" },
                        new ScenePriorityRule { Metadata = "Type", Pattern = "ManualHQWorkflow" },
                        new ScenePriorityRule { Metadata = "Type", Pattern = "AutomaticHQWorkflow" },
                        new ScenePriorityRule { Metadata = "Type", Pattern = "Staging" }
                    }
                },
                Global = new SubtitleGeneratorGlobalSettings
                {
                    // Ex. for 'ManualHQWorkflow-G4\3DSVR-1072\3DSVR-1072- - Mei Miyajima - Night Bus Sex [zalunda].vseq' => '3DSVR-1072 (ManualHQWorkflow-G4)':
                    //   1. ShortName: '<digit?><letters>-<digits>' in the file name (ex. '3DSVR-1072', 'nhdtb-273'),
                    //   2. else ShortName: '<digit?><letters><digits>' in the file name (ex. '3dsvr01866'),
                    //   3. Type: the first folder of the path (ex. 'ManualHQWorkflow-G4', '01-Staging').
                    SceneMetadata = new[]
                    {
                        @"(?<![A-Za-z0-9.])(?<ShortName>\d?[A-Za-z]{2,}-\d+)[^\\]*$",
                        @"(?<![A-Za-z0-9.])(?<ShortName>\d?[A-Za-z]{2,}\d+)[^\\]*$",
                        @"^(?<Type>[^\\]+)\\"
                    },
                    SceneDisplayName = "{ShortName}[ ({Type})]"
                },
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
                    // Transcription of the whole audio (whisper or AI), and its quick translation (to see something early)
                    new TranscriberAudioFull()
                    {
                        Id = "transcription-from-whisper",
                        SourceAudioId = "audio",
                        MetadataProduced = "VoiceText",
                        TranscriberTool = transcriberToolPurfviewWhisper,
                    },
                    new TransformGoogleTranslate()
                    {
                        Id = "quick-translation-of-whisper-using-google",
                        SourceId = "transcription-from-whisper",
                        Produces = "Quick translation",
                        Language = Language.FromString("en"),
                        MetadataNeeded = "VoiceText",
                        MetadataProduced = "TranslatedText"
                    },
                    new TransformAI()
                    {
                        Id = "quick-translation-of-whisper-using-local-api",
                        Produces = "Quick translation",
                        Language = Language.FromString("en"),
                        Engine = defaultEngine,
                        Metadatas = new MetadataAggregator
                        {
                            TimingsSource = "transcription-from-whisper",
                            Sources = "transcription-from-whisper"
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
                        Id = "quick-translation-srt",
                        FileSuffix = ".srt",
                        TextSources = "quick-translation-of-whisper-using-google"
                    },
                    new TranscriberAudioFullAI()
                    {
                        Id = "transcription-from-ai",
                        SourceAudioId = "audio",
                        Engine = defaultEngine,
                        SystemPrompt = transcriberAudioFullSystemPromptForG4,
                        UserPrompt = transcriberAudioFullUserPrompt,
                        MetadataProduced = "VoiceText",
                        MaxChunkDuration = TimeSpan.FromMinutes(30)
                    },
                    // Full transcription by the transcription model of Google: verbatim, with the start / end of each word (good timings);
                    // the subtitles are built from the words. Not used for now (disabled; being tested): a workflow can enable it.
                    new TranscriberAudioFullAIGoogleTranscribe()
                    {
                        Id = "transcription-from-google-ai-transcribe",
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
                        Id = "quick-translation-of-ai-using-google",
                        SourceId = "transcription-from-ai",
                        Produces = "Quick translation",
                        Language = Language.FromString("en"),
                        MetadataNeeded = "VoiceText",
                        MetadataProduced = "TranslatedText"
                    },

                    //---------------------------------------------------
                    // The annotated timings: what will be processed (proposed from the transcription, reviewed by the user, with his annotations)
                    // Prepares the subtitles of 'transcription-from-ai' for the manual-input file: refines their timings (terrain of the audio, + AI depending
                    // on RefineTiming.Engine), adds the title cards, and flags what needs a look ('[REVIEW:Code:Detail] reason' lines).
                    // Enabled by the workflows (with 'transcription' using it as its source).
                    new TransformManualInputPreparer()
                    {
                        Id = "annotated-timings-proposal",
                        Produces = "Proposal",
                        Engine = defaultEngine,
                        PrivateMetadataNames = "TimingTrace",
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "transcription-from-ai",
                            Sources = "transcription-from-ai"
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
                        Id = "transcription",
                        SourceId = "NEED-TO-BE-OVERRIDED" // Should be transcription-from-whisper, transcription-from-ai or annotated-timings-proposal
                    },

                    // (the manual-input file written by hand from the transcription, instead of the Review editor)
                    new OutputSrt()
                    {
                        Id = "annotated-timings-proposal-srt",
                        FileSuffix = ".manual-input.srt",
                        TextSources = "transcription",
                        AddToFirstSubtitle = "[USER-REVISION-NEEDED] <= remove this line when revision is done\n{OngoingContext:The scene take place in...}\n{OngoingSpeakers:Woman}\nOther metadatas to use in the file:\n- GrabOnScreenText\n- VisualTraining (for visual-analyst)"
                    },
                    // The review of the manual-input file in the Review editor of the web UI (button 'Review' of the scene), instead of editing
                    // the file by hand (replaces 'annotated-timings-proposal-srt'). Kept in the .wipsubtitles; when the user clicks 'Review done',
                    // the file is written (then imported by 'annotated-timings'). ReviewNeeded = false: the file is written right away (no review).
                    new TransformReview()
                    {
                        Id = "annotated-timings-review-in-editor",
                        FileSuffix = ".manual-input.srt",
                        // (the text of each source: its own metadata, ex. VoiceTextForManualReview for the preparer, with its review warnings.
                        // Several sources, ex. "annotated-timings-proposal,quick-translation-of-ai-using-google": each one is under a line with its name)
                        TextSources = "annotated-timings-proposal",
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "annotated-timings-proposal",
                            Sources = "annotated-timings-proposal",
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
                        Id = "annotated-timings",
                        Produces = "Annotated timings",
                        FileSuffix = ".manual-input.srt",
                        CanBeUpdated = true,
                        ProcessOnlyWhenStringIsRemoved = "[USER-REVISION-NEEDED]",
                    },
                    // (the times of the annotated timings, frozen; the annotations stay open to changes in the source, that the next steps list)
                    new TransformClone()
                    {
                        Id = "timings",
                        Produces = "Timings",
                        SourceId = "annotated-timings",
                        Metadatas = new MetadataAggregator()
                        {
                            MergeRules = new MergeRuleDictionary() { { "*", null } }
                        }
                    },

                    //---------------------------------------------------
                    // Filling the timings: the analysis and the translation of each one, then the final subtitles
                    // (the older workflows define their own steps, in their override)
                    new TransformAI()
                    {
                        Id = "analysis-and-translation-en",
                        Produces = "Analysis & Translation",
                        Language = Language.FromString("en"),
                        Engine = defaultEngine,
                        ExportMetadataSrt = true,
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "timings",
                            Sources = "transcription,annotated-timings",
                            MergeRules = new MergeRuleDictionary()
                            {
                                { "VoiceText", null },
                                { "VoiceTextValidated", "full-VoiceText" },
                                { "VoiceTextForManualReview", null }
                            }
                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = transcriberAudioSingleVADRefinerSystemPromptForG4,
                            UserPrompt = transcriberAudioSingleVADRefinerUserPromptForG4,
                            MetadataNeeded = "!SkipRefined",
                            MetadataAlwaysProduced = "TranslatedText",
                            BinaryDataExtractors = new BinaryDataExtractor[]
                                {
                                    new BinaryDataExtractorAudio
                                    {
                                        OutputFieldName = "AudioClip",
                                        SourceAudioId = "audio",
                                        MetadataForTraining = "AudioTraining",
                                        MetadataForSkipping = "GrabOnScreenText",
                                        ExpandStart = TimeSpan.FromMilliseconds(200),
                                        ExpandEnd = TimeSpan.FromMilliseconds(300),
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
                                        ContextLongGap = TimeSpan.FromSeconds(30),
                                        KeepTemporaryFiles = true
                                    }
                                },

                            BatchSize = 200,
                            BatchSplitWindows = 15,
                            BatchKeepTogetherThreshold = TimeSpan.FromMilliseconds(30),
                            BatchMaxMBSize = 95,
                            NbContextItems = 10000,
                            NbItemsMinimumReceivedToContinue = 75,
                            FieldsToInclude = NodeFields.StartTime | NodeFields.EndTime,
                            MetadataInContextLimits = new Dictionary<string, int>
                            {
                                { "StartTime", 50 },
                                { "EndTime", 50 },
                                { "TranslationAnalysis", 10 },
                                { "full-VoiceText", 0 },
                                { "VoiceText", 10 },
                                { "OnScreenText", 10 },
                                { "Overlap", 5 }
                            }
                        }
                    },
                    new TransformSubtitleConformer()
                    {
                        Id = "final-subtitles-en",
                        Language = Language.FromString("en"),
                        MetadataSpeaker = "Speaker",
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "timings",
                            Sources = "analysis-and-translation-en,annotated-timings",
                            MergeRules = new MergeRuleDictionary()
                            {
                                { "TranslatedText", "OriginalTranslatedText" }
                            }
                        },
                        Options = new AIOptions()
                        {
                            MetadataNeeded = "OriginalTranslatedText",
                            MetadataAlwaysProduced = "TranslatedText",
                        },
                        MaxMergeGapSameSpeaker = TimeSpan.Zero,
                        MaxMergeGapDialogue = TimeSpan.FromSeconds(0.5),
                        MaxMergeDuration = TimeSpan.FromSeconds(2),
                        RemoveMissing = true,
                        AddReviewIfTooLong = false,
                        SplitPatterns = new[]
                        {
                            // (the end of a sentence, a pause (comma, colon, dash), then the conjunctions, then any space)
                            new TransformSubtitleConformer.SplitPattern(TransformSubtitleConformer.CutWhere.After, @"(?<!\.)\.(?![\d\.])|[?!…]+"),
                            new TransformSubtitleConformer.SplitPattern(TransformSubtitleConformer.CutWhere.After, @"\.{3}|[,:：]+|\s*[-]+\s*"),
                            new TransformSubtitleConformer.SplitPattern(TransformSubtitleConformer.CutWhere.Before, @"\b(because|if|since|while|although|until|unless|so|that)\b"),
                            new TransformSubtitleConformer.SplitPattern(TransformSubtitleConformer.CutWhere.Before, @"\b(what|when|where|how|why)\b"),
                            new TransformSubtitleConformer.SplitPattern(TransformSubtitleConformer.CutWhere.Before, @"\b(with|about|for|from|into)\b"),
                            new TransformSubtitleConformer.SplitPattern(TransformSubtitleConformer.CutWhere.Before, @"\s")
                        }
                    },
                    new OutputSrt()
                    {
                        Id = "final-subtitles-en-srt",
                        TextSources = "final-subtitles-en",
                        FileSuffix = ".final-ai.srt",
                        ExpandSubtileDuration = TimeSpan.FromSeconds(0.3),
                        MinimumSubtitleDuration = TimeSpan.FromSeconds(1.0),
                        SaveFullFileToo = true
                    },
                    new OutputSrt()
                    {
                        Id = "debug-srt",
                        FileSuffix = ".final-ai-debug.srt",
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "timings",
                            Sources = "transcription-from-ai,analysis-and-translation-en,annotated-timings",
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