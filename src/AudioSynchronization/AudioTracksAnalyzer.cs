﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hudl.FFmpeg;
using Hudl.FFmpeg.Attributes;
using Hudl.FFmpeg.Command;
using Hudl.FFmpeg.Resources.BaseTypes;
using Hudl.FFmpeg.Resources.Interfaces;
using Hudl.FFmpeg.Settings;
using Hudl.FFmpeg.Settings.BaseTypes;
using Hudl.FFmpeg.Sugar;

namespace AudioSynchronization
{
    public class AudioTracksAnalyzer
    {
        public AudioTracksAnalyzer()
        {
            ResourceManagement.CommandConfiguration = CommandConfiguration.Create(
                @"C:\!dev\Tools\AutomaticSubtitles\FichiersTest\Temp",
                @"C:\Program Files (NoInstall)\ffmpeg\bin\ffmpeg.exe",
                @"C:\Program Files (NoInstall)\ffmpeg\bin\ffprobe.exe");
        }

        public AudioSignature ExtractSignature(string filename)
        {
            return AudioSignature.FromSamples(ExtractSamples(filename, 120).ToArray());
        }

        private IEnumerable<ushort> ExtractSamples(string filename, int nbSamplesPerSeconds)
        {
            //create a factory 
            var factory = CommandFactory.Create();

            var tempFile = Path.Combine(ResourceManagement.CommandConfiguration.TempPath, "temp.raw");
            File.Delete(tempFile);
            File.Delete(tempFile + ".wav");

            //create a command adding a video file
            var nbSamplesPerPeak = 100;
            var command = factory.CreateOutputCommand()
                .AddInput(filename)
                .To<Raw>(
                    tempFile,
                    SettingsCollection.ForOutput(
                        new ChannelOutput(1),
                        new SampleRate(nbSamplesPerSeconds * nbSamplesPerPeak),
                        new FormatOutput("s16le"),
                        new CodecAudio("pcm_s16le")));

            //render the output
            var result = factory.Render();

            using (var file = File.Open(tempFile, FileMode.Open, FileAccess.Read))
            {
                var nbSampleInCurrentPeak = 0;
                var lowestValue = short.MaxValue;
                var highestValue = short.MinValue;

                while (true)
                {
                    var b1 = file.ReadByte();
                    var b2 = file.ReadByte();
                    if (b1 < 0 || b2 < 0)
                    {
                        yield return (ushort)(highestValue - lowestValue);
                        break;
                    }
                    var value = (short)((b2 << 8) + b1);

                    if (nbSampleInCurrentPeak == nbSamplesPerPeak)
                    {
                        yield return (ushort)(highestValue - lowestValue);
                        nbSampleInCurrentPeak = 0;
                        lowestValue = short.MaxValue;
                        highestValue = short.MinValue;
                    }
                    else
                    {
                        nbSampleInCurrentPeak++;
                        lowestValue = Math.Min(value, lowestValue);
                        highestValue = Math.Max(value, highestValue);
                    }
                }
            }
        }


        [ContainsStream(Type = typeof(AudioStream))]
        private class Raw : BaseContainer
        {
            private const string FileFormat = ".raw";

            public Raw()
                : base(FileFormat)
            {
            }

            protected override IContainer Clone()
            {
                return new Raw();
            }
        }
    }
}
