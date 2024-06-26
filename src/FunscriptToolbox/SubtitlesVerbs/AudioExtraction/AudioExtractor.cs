﻿using System;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.AudioExtraction
{
    public class AudioExtractor
    {
        public string DefaultFfmpegWavParameters { get; set; } = "";

        internal PcmAudio ExtractPcmAudio(SubtitleGeneratorContext context, string inputMp4Fullpath)
        {
            var processStartTime = DateTime.Now;

            var rules = context.CurrentWipsub.SubtitlesForcedTiming?.GetAudioNormalizationRules() ?? Array.Empty<AudioNormalizationRule>();

            var audio = context.FfmpegAudioHelper.ExtractPcmAudio(
                inputMp4Fullpath, 
                (rules.Length > 0) ? string.Empty : this.DefaultFfmpegWavParameters);

            if (context.IsVerbose)
            {
                context.FfmpegAudioHelper.ConvertPcmAudioToWavFile(
                    audio,
                    context.GetPotentialVerboseFilePath("audio-original.wav", processStartTime));
            }

            if (rules.Length > 0)
            {
                var pcmParts = new List<PcmAudio>();
                for (var i = 0; i < rules.Length; i++)
                {
                    var currentRule = rules[i];
                    var partialAudioOriginal = audio.ExtractSnippet(
                        (i > 0)
                        ? currentRule.StartTime
                        : TimeSpan.Zero,
                        (i + 1 < rules.Length)
                        ? rules[i + 1].StartTime
                        : audio.Duration);

                    if (string.IsNullOrEmpty(currentRule.FfmpegParameters?.Trim()))
                    {
                        pcmParts.Add(partialAudioOriginal);
                    }
                    else
                    {
                        var partialAudioNormalized = context
                                .FfmpegAudioHelper
                                .TransformPcmAudio(partialAudioOriginal, currentRule.FfmpegParameters);
                        pcmParts.Add(partialAudioNormalized);
                        if (context.IsVerbose)
                        {
                            context.FfmpegAudioHelper.ConvertPcmAudioToWavFile(
                                partialAudioOriginal,
                                context.GetPotentialVerboseFilePath($"audio-part-{i}-original.wav", processStartTime));
                            context.FfmpegAudioHelper.ConvertPcmAudioToWavFile(
                                partialAudioNormalized,
                                context.GetPotentialVerboseFilePath($"audio-part-{i}-normalized.wav", processStartTime));
                        }
                    }
                }

                audio = new PcmAudio(pcmParts, rules);
                if (context.IsVerbose)
                {
                    context.FfmpegAudioHelper.ConvertPcmAudioToWavFile(
                        audio,
                        context.GetPotentialVerboseFilePath("audio-normalized.wav", processStartTime));
                }
            }
            return audio;
        }
    }
}