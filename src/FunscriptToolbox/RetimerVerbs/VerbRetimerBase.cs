using log4net;
using System;
using System.IO;
using Xabe.FFmpeg;

namespace FunscriptToolbox.RetimerVerbs
{
    internal abstract class VerbRetimerBase : Verb
    {
        protected VerbRetimerBase(ILog log, OptionsBase options) : base(log, options)
        {
        }

        protected IMediaInfo GetMediaInfo(string videoPath)
        {
            if (!File.Exists(videoPath))
                throw new ArgumentException($"Video file '{videoPath}' does not exist.");

            return FFmpeg.GetMediaInfo(videoPath).GetAwaiter().GetResult();
        }
    }
}